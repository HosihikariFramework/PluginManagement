using Hosihikari;
using Hosihikari.Testing;

var checks = new List<(string Name, bool Passed, string Detail)>();

var probe = new ProbePlugin();
using (var host = new PluginTestHost(probe, "Probe"))
{
    host.RunLifecycle();
    checks.Add((
        "生命周期按 Load → Enable → Disable → Unload 驱动",
        host.HookLog.SequenceEqual(["load", "enable", "disable", "unload"]),
        string.Join(" → ", host.HookLog)));
    checks.Add((
        "上下文给出包目录与数据目录",
        Directory.Exists(host.Context.PackageDirectory) && Directory.Exists(host.Context.DataDirectory),
        host.Context.PackageDirectory));
    checks.Add(("插件拿到上下文", probe.Context is not null, probe.Context?.Name ?? "（没有）"));

    var seen = 0;
    var subscription = host.Context.TestEvents.Subscribe<string>(_ => seen++);
    host.Context.TestEvents.Publish("first");
    host.Context.TestEvents.Publish("second");
    subscription.Dispose();
    host.Context.TestEvents.Publish("after-dispose");
    checks.Add(("事件总线订阅与退订", seen == 2, $"seen={seen}"));

    var once = 0;
    host.Context.TestScheduler.ScheduleOnce(TimeSpan.Zero, () => once++);
    host.Context.TestScheduler.RunOnce();
    checks.Add(("一次性调度手动执行", once == 1 && host.Context.TestScheduler.PendingOnce == 0, $"once={once}"));

    var periodic = 0;
    var cancel = host.Context.TestScheduler.SchedulePeriodic(TimeSpan.Zero, () => periodic++);
    host.Context.TestScheduler.RunPeriodic();
    host.Context.TestScheduler.RunPeriodic();
    cancel.Dispose();
    host.Context.TestScheduler.RunPeriodic();
    checks.Add(("周期调度可取消", periodic == 2, $"periodic={periodic}"));

    var services = host.Context.TestServices;
    var first = services.Register<IProbeService>(new ProbeService("first"));
    var duplicate = services.Register<IProbeService>(new ProbeService("second"));
    checks.Add((
        "服务注册先到先得",
        first && !duplicate && services.Get<IProbeService>()?.Name == "first",
        services.Get<IProbeService>()?.Name ?? "（没有）"));

    host.Context.ReportFault("probe fault");
    checks.Add(("故障报告可检查", host.Context.Faults.Contains("probe fault"), string.Join("；", host.Context.Faults)));
}

var refused = new RefusePlugin();
using (var host = new PluginTestHost(refused))
{
    host.Load();
    var result = host.Enable();
    checks.Add((
        "拒绝启用保留原因",
        !result.Allowed && result.Reason == "not now",
        result.Reason ?? "（没有原因）"));
}

Console.WriteLine("=== Hosihikari.Testing 冒烟 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

interface IProbeService
{
    string Name { get; }
}

sealed class ProbeService(string name) : IProbeService
{
    public string Name { get; } = name;
}

sealed class ProbePlugin : IPlugin
{
    public IPluginContext? Context { get; private set; }

    public void OnLoad(IPluginContext context) => Context = context;
    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}

sealed class RefusePlugin : IPlugin
{
    public void OnLoad(IPluginContext context) { }
    public PluginHookResult OnEnable() => PluginHookResult.Refuse("not now");
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}
