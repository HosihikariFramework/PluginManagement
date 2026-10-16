using System.Reflection;
using McMaster.NETCore.Plugins;

namespace Hosihikari.PluginManagement;

/// <summary>内置的程序集型 Provider（ADR-0002）。每个插件一个 collectible ALC，契约走共享层（ADR-0004 / ADR-0019）。</summary>
public sealed class AssemblyPluginProvider(params Assembly[] sharedSources) : IPluginProvider
{
    // 共享层 = 契约 + 当前载体的绑定程序集（ADR-0019）。两侧的类型必须同身份，转型才成立。
    private readonly Type[] _sharedTypes =
    [
        typeof(IPlugin),
        typeof(IPluginContext),
        typeof(PluginManifest),
        .. sharedSources.SelectMany(source => source.GetExportedTypes()),
    ];

    public string Type => "assembly";

    public bool CanLoad(PluginManifest manifest) =>
        manifest.Type == Type && !string.IsNullOrWhiteSpace(manifest.EntryType);

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var entryPath = Path.Combine(packageDirectory, manifest.Entry);

        // isUnloadable 是 ADR-0004；PreferSharedTypes + sharedTypes 是让共享层保持同一身份（ADR-0019）。
        var loader = PluginLoader.CreateFromAssemblyFile(
            entryPath,
            isUnloadable: true,
            sharedTypes: _sharedTypes,
            configure: config => config.PreferSharedTypes = true);

        var assembly = loader.LoadDefaultAssembly();
        var entryType = assembly.GetType(manifest.EntryType!, throwOnError: false);
        if (entryType is null || Activator.CreateInstance(entryType) is not IPlugin plugin)
        {
            loader.Dispose();
            return null;
        }

        return new AssemblyPluginInstance(plugin, loader);
    }
}

internal sealed class AssemblyPluginInstance(IPlugin plugin, PluginLoader loader) : PluginInstance(plugin)
{
    private PluginLoader? _loader = loader;

    public override Func<bool> Release()
    {
        var weakContext = new WeakReference(_loader!.LoadContext);
        _loader.Dispose();
        _loader = null;
        return () => !weakContext.IsAlive;
    }
}
