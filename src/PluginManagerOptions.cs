namespace Hosihikari;

/// <summary>插件管理者的可选配置。现有构造函数保持原样，新配置经重载传入。</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 等待回收验证的时间预算。默认 30 秒；用 <see cref="Timeout.InfiniteTimeSpan"/> 表示不设超时。
    /// 超时按卸载失败记，插件留在待回收（ADR-0042）。
    /// </summary>
    public TimeSpan UnloadVerificationTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
