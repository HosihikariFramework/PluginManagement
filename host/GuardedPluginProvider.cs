using Hosihikari;

namespace Hosihikari.PluginManagement.Spike;

/// <summary>
/// 检验拒绝与故障两条路径（ADR-0030）：拒绝是值，异常与钩子之外的死亡是故障。
/// 三个包共用这一个 Provider，按名字选行为：Guarded 拒绝、Faulty 抛出、Vanishing 异步死亡。
/// </summary>
public sealed class GuardedPluginProvider : IPluginProvider
{
    /// <summary>为真时 <c>Guarded</c> 的启用返回拒绝。</summary>
    public bool RefuseEnable { get; set; }

    /// <summary>为真时 <c>Guarded</c> 的停用返回拒绝。</summary>
    public bool RefuseDisable { get; set; }

    public string Type => "guarded";

    public bool CanLoad(PluginManifest manifest) => manifest.Provider == Type;

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context) =>
        new GuardedPluginInstance(new GuardedPlugin(manifest.Name, this, context));

    private sealed class GuardedPluginInstance(GuardedPlugin plugin) : PluginInstance(plugin)
    {
        public override Func<bool> Release() => static () => true;
    }

    private sealed class GuardedPlugin(string name, GuardedPluginProvider provider, IPluginContext context) : IPlugin
    {
        public void OnLoad(IPluginContext loadedContext)
        {
            if (name == "Vanishing")
            {
                // 模拟 Provider 在钩子之外发现的死亡：钩子返回后，从后台报告故障。
                context.Scheduler.RunLater(
                    TimeSpan.FromMilliseconds(50),
                    () => context.ReportFault("模拟异步死亡"));
            }
        }

        public PluginHookResult OnEnable()
        {
            if (name == "Faulty")
            {
                throw new InvalidOperationException("故意在启用时抛出");
            }

            return provider.RefuseEnable
                ? PluginHookResult.Refuse("现在不行")
                : PluginHookResult.Allow();
        }

        public PluginHookResult OnDisable() =>
            provider.RefuseDisable ? PluginHookResult.Refuse("还不想停") : PluginHookResult.Allow();

        public void OnUnload()
        {
        }
    }
}
