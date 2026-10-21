using Hosihikari;

namespace Hosihikari.Spike;

/// <summary>检验用句柄：没有 ALC 要等，放下插件即为已回收。</summary>
internal sealed class TestPluginHandle(IPlugin plugin) : PluginHandle(plugin)
{
    public override bool IsUnloaded => Plugin is null;

    public override void Unload() => Plugin = null;

    public override Task<bool> WaitForUnloadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsUnloaded);
}
