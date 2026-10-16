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
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _events;
    private readonly IScheduler _scheduler;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly List<IPluginProvider> _providers = [];

    public PluginManagerHost(
        string pluginsRoot,
        string dataRoot,
        string configRoot,
        Action<string> report,
        IServiceProvider? services = null)
    {
        _pluginsRoot = pluginsRoot;
        _dataRoot = dataRoot;
        _configRoot = configRoot;
        _report = report;
        _services = services;
        _loggerFactory = services?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                         ?? NullLoggerFactory.Instance;
        _events = new EventBus(_loggerFactory.CreateLogger("Hosihikari.Events"));
        _scheduler = new Scheduler(_loggerFactory.CreateLogger("Hosihikari.Scheduler"));
    }

    public void AddProvider(IPluginProvider provider) => _providers.Add(provider);

    public IReadOnlyCollection<string> LoadedNames => _plugins.Keys;

    /// <summary>载体适配器要把载体事件并进来，所以事件总线得对外可达（ADR-0007）。</summary>
    public IEventBus Events => _events;

    public void LoadAll()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_configRoot);

        foreach (var package in DependencyGraph.Resolve(ManifestReader.Scan(_pluginsRoot, _report), _report))
        {
            LoadOne(package);
        }
    }

    /// <summary>卸载。有依赖者时**拒绝**并如实返回，不自动级联（ADR-0004）。</summary>
    public UnloadResult Unload(string name)
    {
        if (!_plugins.ContainsKey(name))
        {
            return UnloadResult.Failed($"{name} 未加载");
        }

        var dependents = _plugins.Values
            .Where(p => p.Manifest.Dependencies.Any(d => d.Name == name))
            .Select(p => p.Manifest.Name)
            .ToArray();
        if (dependents.Length > 0)
        {
            return UnloadResult.Failed($"仍有依赖者：{string.Join(", ", dependents)}");
        }

        var verify = Detach(name);

        for (var i = 0; i < 100 && !verify(); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return verify()
            ? UnloadResult.Ok()
            : UnloadResult.Failed($"{name} 的卸载句柄已释放，但回收未验证通过");
    }

    /// <summary>
    /// 停用、卸载、拿走验证器。单独一个不内联的方法，是为了让本方法返回后，
    /// 实例与它的 ALC 在栈上再无强引用 —— 回收验证的前提（ADR-0023）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Func<bool> Detach(string name)
    {
        var entry = _plugins[name];
        _plugins.Remove(name);

        try
        {
            entry.Instance.Plugin.OnDisable();
            entry.Instance.Plugin.OnUnload();
        }
        catch (Exception ex)
        {
            _report($"{name}: 停用钩子抛出 —— {ex.Message}");
        }

        // 作者没退订的订阅、没释放的定时器，在这里一并收回 —— 否则它们把 ALC 钉在共享服务上。
        entry.Scope.ReleaseAll();

        return entry.Instance.Release();
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

    private void LoadOne(PluginPackage package)
    {
        var manifest = package.Manifest;
        var provider = _providers.FirstOrDefault(p => p.CanLoad(manifest));
        if (provider is null)
        {
            _report($"{manifest.Name}: 没有能处理 type={manifest.Type} 的 Provider");
            return;
        }

        var dataDirectory = Path.Combine(_dataRoot, manifest.Name);
        Directory.CreateDirectory(dataDirectory);

        var scope = new PluginScope(_events, _scheduler);
        var context = new PluginContext(
            manifest.Name,
            package.Directory,
            dataDirectory,
            _loggerFactory.CreateLogger(manifest.Name),
            BuildConfiguration(manifest.Name, package.Directory),
            scope,
            _services ?? PluginContext.Empty);

        PluginInstance? instance;
        try
        {
            instance = provider.Load(manifest, package.Directory, context);
        }
        catch (Exception ex)
        {
            // 带完整异常：在别人的进程里没有调试器，消息本身就是唯一的线索（ADR-0009 的可见通道）。
            _report($"{manifest.Name}: 加载抛出 —— {ex}");
            return;
        }

        if (instance is null)
        {
            _report($"{manifest.Name}: 加载失败");
            return;
        }

        try
        {
            instance.Plugin.OnLoad(context);
            instance.Plugin.OnEnable();
        }
        catch (Exception ex)
        {
            _report($"{manifest.Name}: 钩子抛出，已退回 —— {ex.Message}");
            instance.Release();
            return;
        }

        _plugins[manifest.Name] = new LoadedPlugin(manifest, instance, scope);
    }

    private sealed record LoadedPlugin(PluginManifest Manifest, PluginInstance Instance, PluginScope Scope);
}

internal sealed class PluginContext(
    string name,
    string packageDirectory,
    string dataDirectory,
    ILogger logger,
    IConfiguration configuration,
    PluginScope scope,
    IServiceProvider services) : IPluginContext
{
    internal static readonly IServiceProvider Empty = new EmptyServices();

    public string Name => name;

    public string PackageDirectory => packageDirectory;

    public string DataDirectory => dataDirectory;

    public ILogger Logger => logger;

    public IConfiguration Configuration => configuration;

    public IEventBus Events => scope;

    public IScheduler Scheduler => scope;

    public IServiceProvider Services => services;

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
