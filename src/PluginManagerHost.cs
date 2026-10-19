using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hocon.Extensions.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosihikari.PluginManagement;

/// <summary>核心：发现、依赖图、Provider 选型、生命周期驱动、失败隔离（ADR-0009 / 0010）。</summary>
public sealed class PluginManagerHost
{
    private readonly string _pluginsRoot;
    private readonly string _dataRoot;
    private readonly string _configRoot;
    private readonly Action<string> _report;
    private readonly IServiceProvider? _services;
    private readonly Action<string>? _removed;
    private readonly object _gate = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _events;
    private readonly IScheduler _scheduler;
    private readonly PluginServiceRegistry _registry = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<IPluginProvider> _providers = [];

    public PluginManagerHost(
        string pluginsRoot,
        string dataRoot,
        string configRoot,
        Action<string> report,
        IServiceProvider? services = null,
        Action<string>? removed = null)
    {
        _pluginsRoot = pluginsRoot;
        _dataRoot = dataRoot;
        _configRoot = configRoot;
        _report = report;
        _services = services;
        _removed = removed;
        _loggerFactory = services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                         ?? NullLoggerFactory.Instance;
        _events = new EventBus(_loggerFactory.CreateLogger("Hosihikari.Events"));
        _scheduler = new Scheduler(_loggerFactory.CreateLogger("Hosihikari.Scheduler"));

        // 两个根先建出来：父级态下不会走 LoadAll，而插件写数据、运营方放覆盖都指望它们在。
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_configRoot);
    }

    /// <summary>
    /// 登记一个 Provider。同名（<see cref="IPluginProvider.Type"/>）的已经在了就拒掉后一个，
    /// 并把冲突报出来（ADR-0009）—— 静默让后一个落空的话，写它的那个人会以为自己的包被接管了。
    /// </summary>
    /// <returns>登记成功为 <c>true</c>；同名的已在则为 <c>false</c>，先来的那个不变。</returns>
    public bool AddProvider(IPluginProvider provider)
    {
        var existing = _providers.FirstOrDefault(registered => registered.Type == provider.Type);
        if (existing is not null)
        {
            _report($"{provider.Type}: 已经有 {existing.GetType().Name} 处理这个类型，"
                    + $"{provider.GetType().Name} 被拒 —— 插件类型名必须唯一");
            return false;
        }

        _providers.Add(provider);
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
        lock (_gate)
        {
            foreach (var package in DependencyGraph.Resolve(ManifestReader.Scan(_pluginsRoot, _report), _report))
            {
                if (_slots.ContainsKey(package.Manifest.Name))
                {
                    continue;
                }

                var missing = package.Manifest.Dependencies
                    .Where(dependency => dependency.Name != PluginContract.ShimModName)
                    .Where(dependency => !_slots.TryGetValue(dependency.Name, out var slot)
                                         || slot.State != PluginState.Loaded)
                    .Select(dependency => dependency.Name)
                    .ToArray();
                if (missing.Length > 0)
                {
                    _report($"{package.Manifest.Name}: 依赖未加载：{string.Join(", ", missing)}，已跳过");
                    continue;
                }

                LoadOne(package, enable: true);
            }
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
        lock (_gate)
        {
            if (ManifestReader.Read(packageDirectory, _report) is not { } package)
            {
                return false;
            }

            if (_slots.TryGetValue(package.Manifest.Name, out var slot))
            {
                _report($"{package.Manifest.Name}: 已经在册（{slot.State}）");
                return false;
            }

            return LoadOne(package, enable: false);
        }
    }

    /// <summary>
    /// 启用一个在场的插件（ADR-0003：父级能单独启停子插件）。父级决定启停，
    /// 框架只把钩子调到并开关作用域闸门（ADR-0035）。
    /// </summary>
    public bool Enable(string name)
    {
        lock (_gate)
        {
            return DriveHook(name, enable: true);
        }
    }

    /// <summary>
    /// 停用一个在场的插件：先关闭它的作用域闸门，再调 <see cref="IPlugin.OnDisable"/>；
    /// 拒绝时重开（ADR-0035）。订阅与定时器仍保留到**卸载**时才收回。
    /// </summary>
    public bool Disable(string name)
    {
        lock (_gate)
        {
            return DriveHook(name, enable: false);
        }
    }

    private bool DriveHook(string name, bool enable)
    {
        // 已放下、待回收的不算在场 —— 它已经不在跑了，钩子无处可调。
        if (!_slots.TryGetValue(name, out var slot) || slot.State != PluginState.Loaded)
        {
            _report($"{name}: 不在场，{(enable ? "启用" : "停用")}落空");
            return false;
        }

        // 停用先关闸门：OnDisable 执行期间与返回之后都不再开始新的投递；拒绝时恢复。
        var scope = slot.Scope!;
        var wasEnabled = false;
        if (!enable)
        {
            wasEnabled = scope.Close();
        }

        try
        {
            var result = enable
                ? slot.Instance!.Plugin.OnEnable()
                : slot.Instance!.Plugin.OnDisable();
            if (!result.Allowed)
            {
                if (!enable && wasEnabled)
                {
                    scope.Open();
                }

                _report($"{name}: {(enable ? "启用" : "停用")}被拒绝 —— {result.Reason ?? "未给原因"}");
                return false;
            }

            if (enable)
            {
                scope.Open();
            }

            return true;
        }
        catch (Exception ex)
        {
            _report($"{name}: {(enable ? "启用" : "停用")}钩子抛出 —— {ex.Demystify()}");
            ReportFault(name, $"{(enable ? "启用" : "停用")}钩子抛出");
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
        lock (_gate)
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

                var refusal = Detach(slot);
                if (refusal is not null)
                {
                    return UnloadResult.Failed($"{name} 拒绝停用：{refusal}");
                }
            }

            // 到这里 slot 里已经没有指向实例与 ALC 的字段了，可以验回收。
            if (!Collected(slot.Verify!))
            {
                // 说"回收"而不是"ALC"：释放句柄长什么样是 Provider 自己的事（ADR-0010）。
                return UnloadResult.Failed($"{name} 已经放下，但回收验证没通过 —— 再卸载一次会重新验证");
            }

            _slots.Remove(name);
            return UnloadResult.Ok();
        }
    }

    /// <summary>
    /// 故障：钩子抛出，或 Provider 在钩子之外发现插件死了（ADR-0030）。
    /// 由宿主发起卸载；成功后才通知载体摘账。
    /// </summary>
    internal void ReportFault(string name, string reason)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(name, out var slot) || slot.State != PluginState.Loaded)
            {
                return;
            }

            _report($"{name}: 故障 —— {reason}");
            var result = Unload(name);
            if (result.Released)
            {
                _removed?.Invoke(name);
            }
            else
            {
                _report($"{name}: 故障卸载没完成 —— {result.Error}");
            }
        }
    }

    /// <summary>
    /// 停用、卸载、放下实例与作用域，只留下验证器。单独一个不内联的方法，是为了让本方法返回后，
    /// 实例与它的 ALC 在栈上再无强引用 —— 回收验证的前提（ADR-0023）。
    /// 状态与字段一起改完：表里留着实例等于把 ALC 钉死。
    /// 停用被拒绝时返回原因，此时什么都不放开（ADR-0030）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? Detach(Slot slot)
    {
        var instance = slot.Instance!;
        var scope = slot.Scope!;
        var wasEnabled = scope.Close();

        try
        {
            var result = instance.Plugin.OnDisable();
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
            _report($"{slot.Manifest.Name}: 停用钩子抛出 —— {ex.Demystify()}");
        }

        try
        {
            instance.Plugin.OnUnload();
        }
        catch (Exception ex)
        {
            _report($"{slot.Manifest.Name}: 卸载钩子抛出 —— {ex.Demystify()}");
        }

        // 作者没退订的订阅、没释放的定时器，在这里一并收回 —— 否则它们把 ALC 钉在共享服务上。
        scope.ReleaseAll();

        var verify = instance.Release();

        slot.Verify = verify;
        slot.Instance = null;
        slot.Scope = null;
        slot.State = PluginState.Detached;
        return null;
    }

    /// <summary>回收验证：只攥着验证器，它内部是一个 WeakReference。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collected(Func<bool> verify)
    {
        for (var i = 0; i < 100 && !verify(); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return verify();
    }

    /// <summary>
    /// 包内默认 → 包外覆盖 → 环境变量，后写覆盖先写（ADR-0018）。
    /// 三段都可选 —— 完全不带配置的插件是正常情况（ADR-0009）。
    /// 分层**必须**走 provider 链：HOCON 的 <c>include</c> 在这条路径上会被静默忽略。
    /// </summary>
    private IConfiguration BuildConfiguration(string name, string packageDirectory) =>
        new ConfigurationBuilder()
            .AddHoconFile(Path.Combine(packageDirectory, $"{name}.conf"), true)
            .AddHoconFile(Path.Combine(_configRoot, $"{name}.conf"), true)
            .AddEnvironmentVariables()
            .Build();

    private bool LoadOne(PluginPackage package, bool enable)
    {
        var manifest = package.Manifest;
        var scope = new PluginScope(_events, _scheduler, _registry);
        PluginInstance? instance = null;
        var loadStarted = false;
        var enableStarted = false;
        var loaded = false;
        try
        {
            var provider = _providers.FirstOrDefault(p => p.CanLoad(manifest));
            if (provider is null)
            {
                _report($"{manifest.Name}: 没有能处理 provider={manifest.Provider} 的 Provider");
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
                _services ?? PluginContext.Empty,
                this);

            instance = provider.Load(manifest, package.Directory, context);
            if (instance is null)
            {
                _report($"{manifest.Name}: 加载失败");
                return false;
            }

            loadStarted = true;
            instance.Plugin.OnLoad(context);
            if (enable)
            {
                enableStarted = true;
                var result = instance.Plugin.OnEnable();
                if (!result.Allowed)
                {
                    _report($"{manifest.Name}: 启用被拒绝 —— {result.Reason ?? "未给原因"}（留在册、未启用）");
                }
                else
                {
                    scope.Open();
                }
            }

            _slots[manifest.Name] = new Slot(manifest) { Instance = instance, Scope = scope };
            loaded = true;
            return true;
        }
        catch (Exception ex)
        {
            _report($"{manifest.Name}: 加载抛出，已退回 —— {ex.Demystify()}");
            return false;
        }
        finally
        {
            if (!loaded)
            {
                if (enableStarted)
                {
                    Cleanup(() => instance!.Plugin.OnDisable());
                }

                if (loadStarted)
                {
                    Cleanup(() => instance!.Plugin.OnUnload());
                }

                Cleanup(scope.ReleaseAll);
                if (instance is not null)
                {
                    Cleanup(() => instance.Release());
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
                _report($"{manifest.Name}: 加载回滚抛出 —— {ex.Demystify()}");
            }
        }
    }

    /// <summary>
    /// 表里的一格。两段式卸载要求它在状态切换时把指向实例与 ALC 的字段一并清掉（ADR-0023）——
    /// 所以这里放的是可空字段而不是 record。
    /// </summary>
    private sealed class Slot(PluginManifest manifest)
    {
        public PluginManifest Manifest { get; } = manifest;

        public PluginState State { get; set; } = PluginState.Loaded;

        /// <summary>只在 <see cref="PluginState.Loaded"/> 时在场。</summary>
        public PluginInstance? Instance { get; set; }

        public PluginScope? Scope { get; set; }

        /// <summary>只在 <see cref="PluginState.Detached"/> 时在场。它只攥着一个 WeakReference，不钉住 ALC。</summary>
        public Func<bool>? Verify { get; set; }
    }
}

/// <summary>插件在框架里的状态。</summary>
internal enum PluginState
{
    /// <summary>在场：实例已建、钩子已挂，事件订阅与定时器归它的作用域管。</summary>
    Loaded,

    /// <summary>已放下：实例与作用域都放开了，只等 ALC 被真正回收。回收没通过前它仍占着内存（ADR-0004）。</summary>
    Detached,
}

internal sealed class PluginContext(
    string name,
    string packageDirectory,
    string dataDirectory,
    ILogger logger,
    IConfiguration configuration,
    PluginScope scope,
    IServiceProvider services,
    PluginManagerHost host) : IPluginContext
{
    internal static readonly IServiceProvider Empty = new EmptyServices();

    public string Name => name;

    public string PackageDirectory => packageDirectory;

    public string DataDirectory => dataDirectory;

    public ILogger Logger => logger;

    public IConfiguration Configuration => configuration;

    public IEventBus Events => scope;

    public IScheduler Scheduler => scope;

    public IPluginServiceRegistry PluginServices => scope;

    public IServiceProvider Services => services;

    /// <summary>Provider 在钩子之外发现插件死了时走这里（ADR-0030）。</summary>
    public void ReportFault(string reason) => host.ReportFault(name, reason);

    /// <summary>Provider 是个插件包，装载时用这个把自己交出去（ADR-0002）。</summary>
    public void RegisterProvider(IPluginProvider provider) => host.AddProvider(provider);

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
