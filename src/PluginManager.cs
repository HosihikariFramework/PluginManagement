using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hocon.Extensions.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hosihikari.Internal;

namespace Hosihikari;

/// <summary>核心：发现、依赖图、加载器选型、生命周期驱动、失败隔离（ADR-0009 / 0010）。</summary>
public sealed class PluginManager
{
    private readonly string _pluginsRoot;
    private readonly string _dataRoot;
    private readonly string _configRoot;
    private readonly Action<string> _reportFault;
    private readonly IServiceProvider? _carrierServices;
    private readonly Action<string>? _onRemoved;
    private readonly object _gate = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _events;
    private readonly IScheduler _scheduler;
    private readonly PluginServiceRegistry _registry = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _loading = [];
    private readonly Dictionary<Guid, string> _pendingFaults = [];
    private readonly HashSet<Guid> _loaderIds = [];
    private readonly Dictionary<Guid, List<IPluginLoader>> _registeredLoaders = [];
    private readonly List<IPluginLoader> _loaders = [];

    public PluginManager(
        string pluginsRoot,
        string dataRoot,
        string configRoot,
        Action<string> reportFault,
        IServiceProvider? carrierServices = null,
        Action<string>? onRemoved = null)
    {
        _pluginsRoot = pluginsRoot;
        _dataRoot = dataRoot;
        _configRoot = configRoot;
        _reportFault = reportFault;
        _carrierServices = carrierServices;
        _onRemoved = onRemoved;
        _loggerFactory = carrierServices?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                         ?? NullLoggerFactory.Instance;
        _events = new EventBus(_loggerFactory.CreateLogger("Hosihikari.Events"));
        _scheduler = new Scheduler(_loggerFactory.CreateLogger("Hosihikari.Scheduler"));

        // 两个根先建出来：父级态下不会走 LoadAll，而插件写数据、运营方放覆盖都指望它们在。
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_configRoot);
    }

    /// <summary>
    /// 登记一个加载器。同名（<see cref="IPluginLoader.Id"/>）的已经在了就拒掉后一个，
    /// 并把冲突报出来（ADR-0009）—— 静默让后一个落空的话，写它的那个人会以为自己的包被接管了。
    /// </summary>
    /// <returns>登记成功为 <c>true</c>；同名的已在则为 <c>false</c>，先来的那个不变。</returns>
    public bool RegisterLoader(IPluginLoader loader)
    {
        var added = TryAddLoader(loader, out var problem);
        if (problem is not null)
        {
            _reportFault(problem);
        }

        return added;
    }

    /// <summary>
    /// 把一次登记归到具体的装载实例上；重载时据此保留加载器，不保留普通子插件。
    /// </summary>
    internal void MarkLoader(Guid instanceId, IPluginLoader loader)
    {
        lock (_gate)
        {
            if (_slots.Values.FirstOrDefault(slot => slot.Id == instanceId) is { } loaded)
            {
                loaded.IsLoader = true;
            }
            else
            {
                _loaderIds.Add(instanceId);
            }

            if (!_registeredLoaders.TryGetValue(instanceId, out var registered))
            {
                _registeredLoaders[instanceId] = registered = [];
            }

            registered.Add(loader);
        }
    }

    /// <summary>加载失败时撤掉本次装载登记过的加载器，别让死包继续接管路由。</summary>
    private void RemoveLoaders(Guid instanceId)
    {
        lock (_gate)
        {
            _loaderIds.Remove(instanceId);
            if (_registeredLoaders.Remove(instanceId, out var registered))
            {
                foreach (var loader in registered)
                {
                    _loaders.Remove(loader);
                }
            }
        }
    }

    /// <summary>
    /// 不向父级回报的登记。加载路径持锁时走这里，冲突文本交给加载报告统一带出去。
    /// </summary>
    internal bool TryAddLoader(IPluginLoader loader, out string? problem)
    {
        var existing = _loaders.FirstOrDefault(registered => registered.Id == loader.Id);
        if (existing is not null)
        {
            problem = $"{loader.Id}: 已经有 {existing.GetType().Name} 处理这个种类，"
                      + $"{loader.GetType().Name} 被拒 —— 插件种类名必须唯一";
            return false;
        }

        _loaders.Add(loader);
        problem = null;
        return true;
    }

    /// <summary>在场的插件名。已经放下、只等回收的那些不算（见 <see cref="PluginState"/>）。</summary>
    public IReadOnlyCollection<string> LoadedNames
    {
        get
        {
            lock (_gate)
            {
                return _slots.Where(slot => slot.Value.State == PluginState.Loaded)
                    .Select(slot => slot.Key)
                    .ToArray();
            }
        }
    }

    /// <summary>载体适配器要把载体事件并进来，所以事件总线得对外可达（ADR-0007）。</summary>
    public IEventBus Events => _events;

    /// <summary>自持态：插件包就在 <c>plugins/</c> 下，由我们枚举、按依赖排序后逐个装（ADR-0018）。</summary>
    public void LoadAll()
    {
        var reports = new List<string>();
        try
        {
            lock (_gate)
            {
                foreach (var package in DependencyGraph.Resolve(ManifestReader.Scan(_pluginsRoot, reports.Add), reports.Add))
                {
                    if (_slots.ContainsKey(package.Manifest.Name))
                    {
                        continue;
                    }

                    var missing = package.Manifest.Dependencies
                        .Where(dependency => dependency.Name != PluginContractInfo.ParentName)
                        .Where(dependency => !_slots.TryGetValue(dependency.Name, out var slot)
                                             || slot.State != PluginState.Loaded)
                        .Select(dependency => dependency.Name)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        reports.Add($"{package.Manifest.Name}: 依赖未加载：{string.Join(", ", missing)}，已跳过");
                        continue;
                    }

                    LoadOne(package, enable: true, reports);
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }
    }

    /// <summary>
    /// 父级态：父级把包逐个交过来（ADR-0003）。顺序与依赖由父级的计划保证，我们只管装。
    /// 返回它有没有装上 —— 失败原因已经进了日志。
    ///
    /// **不在这里启用**：启停由父级驱动（<see cref="IPlugin.OnDisable"/> 的约定），
    /// 父级自己的启用阶段随后会经 <see cref="Enable"/> 把钩子调到。这里先启用的话，
    /// 插件会被启用两次，而中间那段的"已启用"是父级不知道的。
    /// </summary>
    public bool Load(string packageDirectory)
    {
        var reports = new List<string>();
        var result = false;
        string? reusedLoader = null;
        try
        {
            lock (_gate)
            {
                if (ManifestReader.Read(packageDirectory, reports.Add) is not { } package)
                {
                    result = false;
                }
                else if (_slots.TryGetValue(package.Manifest.Name, out var slot))
                {
                    if (slot.IsLoader && slot.State == PluginState.Loaded)
                    {
                        reusedLoader = package.Manifest.Name;
                        result = true;
                    }
                    else
                    {
                        reports.Add($"{package.Manifest.Name}: 已经在册（{slot.State}）");
                        result = false;
                    }
                }
                else if (DependencyProblem(package.Manifest) is { } dependencyProblem)
                {
                    reports.Add($"{package.Manifest.Name}: {dependencyProblem}");
                    result = false;
                }
                else
                {
                    result = LoadOne(package, enable: false, reports);
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }

        // 父级的日志回调可能拿父级的锁；出了 _gate 再写，避免与卸载路径互相等。
        if (reusedLoader is not null)
        {
            _loggerFactory.CreateLogger("Hosihikari")
                .LogInformation("{Name}: 加载器已加载，复用", reusedLoader);
        }

        return result;
    }

    /// <summary>
    /// 启用一个在场的插件（ADR-0003：父级能单独启停子插件）。父级决定启停，
    /// 框架只把钩子调到并开关作用域闸门（ADR-0035）。
    /// </summary>
    public bool Enable(string name)
    {
        List<string>? reports = null;
        Slot? faulted = null;
        string? fault = null;
        bool result;
        lock (_gate)
        {
            result = DriveHook(name, enable: true, ref reports, out faulted, out fault);
        }

        ReportAll(reports);
        if (faulted is not null)
        {
            ReportFault(faulted.Manifest.Name, faulted.Id, fault!);
        }

        return result;
    }

    /// <summary>
    /// 停用一个在场的插件：先关闭它的作用域闸门，再调 <see cref="IPlugin.OnDisable"/>；
    /// 拒绝时重开（ADR-0035）。订阅与定时器仍保留到**卸载**时才收回。
    /// </summary>
    public bool Disable(string name)
    {
        List<string>? reports = null;
        Slot? faulted = null;
        string? fault = null;
        bool result;
        lock (_gate)
        {
            result = DriveHook(name, enable: false, ref reports, out faulted, out fault);
        }

        ReportAll(reports);
        if (faulted is not null)
        {
            ReportFault(faulted.Manifest.Name, faulted.Id, fault!);
        }

        return result;
    }

    private bool DriveHook(
        string name,
        bool enable,
        ref List<string>? reports,
        out Slot? faulted,
        out string? fault)
    {
        fault = null;
        faulted = null;

        // 已放下、待回收的不算在场 —— 它已经不在跑了，钩子无处可调。
        if (!_slots.TryGetValue(name, out var slot) || slot.State != PluginState.Loaded)
        {
            (reports ??= []).Add($"{name}: 不在场，{(enable ? "启用" : "停用")}落空");
            return false;
        }

        if (enable && slot.Enabled)
        {
            (reports ??= []).Add($"{name}: 已经启用，忽略重复请求");
            return true;
        }

        // 停用先关闸门：OnDisable 执行期间与返回之后都不再开始新的投递；拒绝时恢复。
        var scope = slot.Scope!;
        var wasEnabled = slot.Enabled;
        scope.Close();

        try
        {
            var result = enable
                ? slot.Handle!.Plugin!.OnEnable()
                : slot.Handle!.Plugin!.OnDisable();
            if (!result.Allowed)
            {
                if (wasEnabled && !enable)
                {
                    scope.Open();
                }

                (reports ??= []).Add($"{name}: {(enable ? "启用" : "停用")}被拒绝 —— {result.Reason ?? "未给原因"}");
                return false;
            }

            slot.Enabled = enable;
            if (enable)
            {
                scope.Open();
            }

            return true;
        }
        catch (Exception ex)
        {
            (reports ??= []).Add($"{name}: {(enable ? "启用" : "停用")}钩子抛出 —— {ex.Demystify()}");
            faulted = slot;
            fault = $"{(enable ? "启用" : "停用")}钩子抛出";
            return false;
        }
    }

    /// <summary>
    /// 卸载。有依赖者时**拒绝**并如实返回，不自动级联（ADR-0004）。
    ///
    /// 卸载分两段：先把实例与作用域放下，再等 ALC 被回收。第二段没通过时状态停在
    /// <see cref="PluginState.Detached"/> 并如实回报失败 —— 再问一次就是再验一次
    /// （钉住 ALC 的东西可能是暂时的，重试真的可能通过）。
    /// </summary>
    public UnloadResult Unload(string name)
    {
        List<string>? reports = null;
        UnloadResult result;
        lock (_gate)
        {
            // 加载器的登记不能撤销：它的类型住在自己的 ALC 里，放掉实例会让路由指向死代码。
            result = _slots.TryGetValue(name, out var slot) && slot.IsLoader
                ? UnloadResult.Failed($"{name}: 加载器登记不能撤销；更换加载器需要重启服务端")
                : UnloadLocked(name, ref reports);
        }

        ReportAll(reports);
        return result;
    }

    /// <summary>父级态下的依赖检查：目标已在场，且版本满足清单约束（ADR-0039）。</summary>
    private string? DependencyProblem(PluginManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.Name == PluginContractInfo.ParentName)
            {
                continue;
            }

            if (!_slots.TryGetValue(dependency.Name, out var slot) || slot.State != PluginState.Loaded)
            {
                return $"依赖未加载：{dependency.Name}";
            }

            if (!DependencyVersion.Satisfies(slot.Manifest, dependency, out var problem))
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// 载体重载前的子插件回收。Loader 包按 ADR-0002 不能撤销登记，跨 reload 保留；
    /// 它们造出来的实例与路由也留着，供新 loader 直接复用。
    /// </summary>
    internal void UnloadChildren()
    {
        List<string>? reports = [];
        try
        {
            lock (_gate)
            {
                foreach (var name in _slots.Keys.Reverse().ToArray())
                {
                    if (_slots.TryGetValue(name, out var slot) && slot.IsLoader)
                    {
                        continue;
                    }

                    var result = UnloadLocked(name, ref reports);
                    var error = result.Error;
                    if (!result.Released && error is not null)
                    {
                        reports?.Add($"{name}: {error}");
                    }
                }
            }
        }
        finally
        {
            ReportAll(reports);
        }
    }

    /// <summary>
    /// 卸载的加锁部分。报告先攒着，出了锁再发 —— 父级的日志回调可能拿父级自己的锁，
    /// 而父级线程可能正握着那把锁等 <see cref="_gate"/>；在锁内回调会互相等死。
    /// </summary>
    private UnloadResult UnloadLocked(string name, ref List<string>? reports)
    {
        if (!_slots.TryGetValue(name, out var slot))
        {
            return UnloadResult.Failed($"{name} 未加载");
        }

        if (slot.State == PluginState.Loaded)
        {
            var dependents = _slots.Values
                .Where(loaded => loaded.State == PluginState.Loaded)
                .Where(p => p.Manifest.Dependencies.Any(d => d.Name == name))
                .Select(p => p.Manifest.Name)
                .ToArray();
            if (dependents.Length > 0)
            {
                return UnloadResult.Failed($"仍有依赖者：{string.Join(", ", dependents)}");
            }

            var refusal = Detach(slot, ref reports);
            if (refusal is not null)
            {
                return UnloadResult.Failed($"{name} 拒绝停用：{refusal}");
            }
        }

        // 到这里 slot 里已经没有指向插件与 ALC 的字段了，可以验回收。
        if (!Collected(slot.Handle!))
        {
            // 说"回收"而不是"ALC"：释放句柄长什么样是 Loader 自己的事（ADR-0010）。
            return UnloadResult.Failed($"{name} 已经放下，但回收验证没通过 —— 再卸载一次会重新验证");
        }

        _slots.Remove(name);
        return UnloadResult.Ok();
    }

    private void ReportAll(List<string>? reports)
    {
        if (reports is null)
        {
            return;
        }

        foreach (var report in reports)
        {
            _reportFault(report);
        }
    }

    /// <summary>
    /// 故障：钩子抛出，或 Loader 在钩子之外发现插件死了（ADR-0030）。
    /// 由宿主发起卸载；成功后才通知载体摘账。
    /// 故障只属于发起它的那个实例：报告在锁外发，期间同名插件可能已经重新装载，
    /// 按名字找会把健康的新实例连坐卸掉。所以这里认的是装载时发给上下文的编号。
    /// 上下文只带名字与编号 —— 带上槽位或委托会把实例经闭包钉住，ALC 就回收不了。
    /// </summary>
    internal void ReportFault(string name, Guid instanceId, string reason)
    {
        List<string>? reports = null;
        UnloadResult result;
        var present = false;
        lock (_gate)
        {
            if (_slots.TryGetValue(name, out var current)
                && current.Id == instanceId
                && current.State == PluginState.Loaded)
            {
                present = true;
                (reports ??= []).Add($"{name}: 故障 —— {reason}");
                result = UnloadLocked(name, ref reports);
            }
            else if (_loading.TryGetValue(instanceId, out var loadingName) && loadingName == name)
            {
                // 装载钩子还没跑完，实例尚未交给表。记下来，让 LoadOne 在回滚时一并报。
                _pendingFaults[instanceId] = reason;
                return;
            }
            else
            {
                result = UnloadResult.Failed($"{name} 未加载");
            }
        }

        // 报告与摘账都在锁外：它们会回到父级，而父级的锁可能正被等 _gate 的线程握着。
        ReportAll(reports);
        if (!present)
        {
            return;
        }

        if (result.Released)
        {
            _onRemoved?.Invoke(name);
        }
        else
        {
            _reportFault($"{name}: 故障卸载没完成 —— {result.Error}");
        }
    }

    /// <summary>
    /// 停用、卸载、放下句柄与作用域，只留下回收验证。单独一个不内联的方法，是为了让本方法返回后，
    /// 插件与它的 ALC 在栈上再无强引用 —— 回收验证的前提（ADR-0023）。
    /// 状态与字段一起改完：表里留着插件等于把 ALC 钉死。
    /// 停用被拒绝时返回原因，此时什么都不放开（ADR-0030）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? Detach(Slot slot, ref List<string>? reports)
    {
        var handle = slot.Handle!;
        var plugin = handle.Plugin!;
        var scope = slot.Scope!;
        var wasEnabled = scope.Close();

        try
        {
            var result = plugin.OnDisable();
            if (!result.Allowed)
            {
                if (wasEnabled)
                {
                    scope.Open();
                }

                return result.Reason ?? "未给原因";
            }
        }
        catch (Exception ex)
        {
            (reports ??= []).Add($"{slot.Manifest.Name}: 停用钩子抛出 —— {ex.Demystify()}");
        }

        try
        {
            plugin.OnUnload();
        }
        catch (Exception ex)
        {
            (reports ??= []).Add($"{slot.Manifest.Name}: 卸载钩子抛出 —— {ex.Demystify()}");
        }

        // 作者没退订的订阅、没释放的定时器，在这里一并收回 —— 否则它们把 ALC 钉在共享服务上。
        scope.ReleaseAll();

        try
        {
            handle.Unload();
        }
        catch (Exception ex)
        {
            // 放下失败仍要继续验回收：句柄可能已经放掉一部分，状态机照实记录。
            (reports ??= []).Add($"{slot.Manifest.Name}: 放下句柄抛出 —— {ex.Demystify()}");
        }

        slot.Scope = null;
        slot.State = PluginState.Detached;
        return null;
    }

    /// <summary>回收验证：句柄只攥着 WeakReference，不钉住 ALC。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collected(PluginHandle handle) =>
        handle.WaitForUnloadAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 包内默认 → 包外覆盖 → 环境变量，后写覆盖先写（ADR-0018）。
    /// 三段都可选 —— 完全不带配置的插件是正常情况（ADR-0009）。
    /// 分层**必须**走配置源链：HOCON 的 <c>include</c> 在这条路径上会被静默忽略。
    /// </summary>
    private IConfiguration BuildConfiguration(string name, string packageDirectory) =>
        new ConfigurationBuilder()
            .AddHoconFile(Path.Combine(packageDirectory, $"{name}.conf"), true)
            .AddHoconFile(Path.Combine(_configRoot, $"{name}.conf"), true)
            .AddEnvironmentVariables()
            .Build();

    private bool LoadOne(PluginPackage package, bool enable, List<string> reports)
    {
        var manifest = package.Manifest;
        var scope = new PluginScope(_events, _scheduler, _registry);
        // 先建这一格，让上下文报告故障时认的是本次装载，而不是后面可能顶替它的同名实例。
        var slot = new Slot(manifest);
        _loading[slot.Id] = manifest.Name;
        PluginHandle? handle = null;
        var loadStarted = false;
        var enableStarted = false;
        var enabled = false;
        var loaded = false;
        try
        {
            var loader = _loaders.FirstOrDefault(candidate => candidate.CanLoad(manifest));
            if (loader is null)
            {
                reports.Add($"{manifest.Name}: 没有能处理 loader={manifest.Loader} 的加载器");
                return false;
            }

            var dataDirectory = Path.Combine(_dataRoot, manifest.Name);
            Directory.CreateDirectory(dataDirectory);
            var context = new PluginContext(
                manifest.Name,
                package.Directory,
                dataDirectory,
                _loggerFactory.CreateLogger(manifest.Name),
                BuildConfiguration(manifest.Name, package.Directory),
                scope,
                _carrierServices ?? PluginContext.Empty,
                this,
                slot.Id,
                reports.Add);

            handle = loader.Load(manifest, package.Directory, context);
            if (handle?.Plugin is null)
            {
                reports.Add($"{manifest.Name}: 加载失败");
                return false;
            }

            loadStarted = true;
            handle.Plugin.OnLoad(context);
            if (_pendingFaults.TryGetValue(slot.Id, out var loadFault))
            {
                reports.Add($"{manifest.Name}: 故障 —— {loadFault}");
                return false;
            }

            if (enable)
            {
                enableStarted = true;
                var result = handle.Plugin.OnEnable();
                if (!result.Allowed)
                {
                    reports.Add($"{manifest.Name}: 启用被拒绝 —— {result.Reason ?? "未给原因"}（留在册、未启用）");
                }
                else
                {
                    scope.Open();
                    enabled = true;
                }
            }

            // 装载期间同一线程报告的故障会落在这里；实例还没交给表，直接回滚。
            if (_pendingFaults.TryGetValue(slot.Id, out var pendingFault))
            {
                reports.Add($"{manifest.Name}: 故障 —— {pendingFault}");
                return false;
            }

            slot.IsLoader = _loaderIds.Remove(slot.Id);
            slot.Enabled = enabled;
            slot.Handle = handle;
            slot.Scope = scope;
            _slots[manifest.Name] = slot;
            loaded = true;
            return true;
        }
        catch (Exception ex)
        {
            reports.Add($"{manifest.Name}: 加载抛出，已退回 —— {ex.Demystify()}");
            return false;
        }
        finally
        {
            _loading.Remove(slot.Id);
            _pendingFaults.Remove(slot.Id);
            if (!loaded)
            {
                RemoveLoaders(slot.Id);
            }
            else
            {
                _loaderIds.Remove(slot.Id);
            }
            if (!loaded)
            {
                if (enableStarted)
                {
                    Cleanup(() => handle!.Plugin!.OnDisable());
                }

                if (loadStarted)
                {
                    Cleanup(() => handle!.Plugin!.OnUnload());
                }

                Cleanup(scope.ReleaseAll);
                if (handle is not null)
                {
                    Cleanup(handle.Unload);
                }
            }
        }

        void Cleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                reports.Add($"{manifest.Name}: 加载回滚抛出 —— {ex.Demystify()}");
            }
        }
    }

    /// <summary>
    /// 表里的一格。两段式卸载要求它在状态切换时把指向插件与 ALC 的字段一并清掉（ADR-0023）——
    /// 所以这里放的是可空字段而不是 record。
    /// </summary>
    private sealed class Slot(PluginManifest manifest)
    {
        public PluginManifest Manifest { get; } = manifest;

        /// <summary>本次装载的编号：故障报告认它，同名重载后的新实例编号不同。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        public PluginState State { get; set; } = PluginState.Loaded;

        /// <summary>在场时指向插件；放下后仍留着，用来做回收验证（它只攥一个 WeakReference）。</summary>
        public PluginHandle? Handle { get; set; }

        public PluginScope? Scope { get; set; }

        /// <summary>Loader 包；载体重载时保留，供新 loader 直接复用。</summary>
        public bool IsLoader { get; set; }

        /// <summary>启用状态；重复 Enable/Disable 不能把闸门留在错误的一侧。</summary>
        public bool Enabled { get; set; }
    }
}
