using System.Runtime.CompilerServices;

namespace Hosihikari.PluginManagement;

/// <summary>核心：发现、依赖图、Provider 选型、生命周期驱动、失败隔离（ADR-0009 / 0010）。</summary>
public sealed class PluginManagerHost(
    string pluginsRoot,
    string dataRoot,
    Action<string> report,
    IServiceProvider? services = null)
{
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly List<IPluginProvider> _providers = [];

    public void AddProvider(IPluginProvider provider) => _providers.Add(provider);

    public IReadOnlyCollection<string> LoadedNames => _plugins.Keys;

    public void LoadAll()
    {
        foreach (var package in DependencyGraph.Resolve(ManifestReader.Scan(pluginsRoot, report), report))
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
    /// 实例与它的 ALC 在栈上再无强引用 —— 回收验证的前提（ADR-0004）。
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
            report($"{name}: 停用钩子抛出 —— {ex.Message}");
        }

        return entry.Instance.Release();
    }

    private void LoadOne(PluginPackage package)
    {
        var manifest = package.Manifest;
        var provider = _providers.FirstOrDefault(p => p.CanLoad(manifest));
        if (provider is null)
        {
            report($"{manifest.Name}: 没有能处理 type={manifest.Type} 的 Provider");
            return;
        }

        var dataDirectory = Path.Combine(dataRoot, manifest.Name);
        Directory.CreateDirectory(dataDirectory);
        var context = new PluginContext(manifest.Name, package.Directory, dataDirectory, services ?? PluginContext.Empty);

        PluginInstance? instance;
        try
        {
            instance = provider.Load(manifest, package.Directory, context);
        }
        catch (Exception ex)
        {
            report($"{manifest.Name}: 加载抛出 —— {ex.Message}");
            return;
        }

        if (instance is null)
        {
            report($"{manifest.Name}: 加载失败");
            return;
        }

        try
        {
            instance.Plugin.OnLoad(context);
            instance.Plugin.OnEnable();
        }
        catch (Exception ex)
        {
            report($"{manifest.Name}: 钩子抛出，已退回 —— {ex.Message}");
            instance.Release();
            return;
        }

        _plugins[manifest.Name] = new LoadedPlugin(manifest, instance);
    }

    private sealed record LoadedPlugin(PluginManifest Manifest, PluginInstance Instance);
}

internal sealed class PluginContext(
    string name,
    string packageDirectory,
    string dataDirectory,
    IServiceProvider services) : IPluginContext
{
    internal static readonly IServiceProvider Empty = new EmptyServices();

    public string Name => name;

    public string PackageDirectory => packageDirectory;

    public string DataDirectory => dataDirectory;

    public IServiceProvider Services => services;

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
