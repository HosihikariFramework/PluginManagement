using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>
/// 一个"外面还有根指着它"的插件：在 <see cref="Pinned"/> 放开之前，回收验证不会通过。
///
/// 真实的钉法是 ALC 之外的一个根（没退出的线程、共享层里的静态、宿主侧的静态），
/// 真实的 ALC 回收纪律由 A3 那条覆盖。这条要钉的是宿主的状态机：
/// 放下之后照实报失败、状态停在待回收，再问一次是**重新验证**而不是"未加载"。
/// </summary>
public sealed class PinnedPluginLoader : IPluginLoader
{
    /// <summary>为真时模拟"外面还有根"（ADR-0004 里作者欠的那笔账）。</summary>
    public bool Pinned { get; set; } = true;

    public string Id => "pinned";

    public bool CanLoad(PluginManifest manifest) => manifest.Loader == Id;

    public PluginHandle? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        new PinnedPluginHandle(new PinnedPlugin(), this);
}

internal sealed class PinnedPluginHandle(PinnedPlugin plugin, PinnedPluginLoader loader)
    : PluginHandle(plugin)
{
    private bool _unloadStarted;

    public override bool IsUnloaded => _unloadStarted && !loader.Pinned;

    /// <summary>只攥着加载器；插件实例在放下后立即松手 —— 与真实句柄只攥一个 WeakReference 同理。</summary>
    public override void Unload()
    {
        _unloadStarted = true;
        Plugin = null;
    }

    public override async Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_unloadStarted)
        {
            return false;
        }

        for (var i = 0; i < 100 && loader.Pinned; i++)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return !loader.Pinned;
    }
}

internal sealed class PinnedPlugin : IPlugin
{
    private string? _dataDirectory;

    public void OnLoad(IPluginContext context)
    {
        _dataDirectory = context.DataDirectory;
        Record("loaded");
    }

    public PluginHookResult OnEnable()
    {
        Record("enabled");
        return PluginHookResult.Allow();
    }

    public PluginHookResult OnDisable()
    {
        Record("disabled");
        return PluginHookResult.Allow();
    }

    public void OnUnload() => Record("unloaded");

    /// <summary>把最后一个到达的钩子写下来 —— 断言据此看"谁在驱动"（ADR-0003）。</summary>
    private void Record(string hook)
    {
        if (_dataDirectory is not null)
        {
            File.WriteAllText(Path.Combine(_dataDirectory, "hook.txt"), hook);
        }
    }
}
