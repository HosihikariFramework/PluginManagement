using Hosihikari;
using Hosihikari.Bindings;
using Microsoft.Extensions.Logging;

[assembly: EntryPoint<Foo.FooPlugin>]

namespace Foo;

public sealed class FooPlugin : IPlugin
{
    public void OnLoad(IPluginContext context)
    {
        context.Logger.LogInformation("OnLoad  package={Package}", context.PackageDirectory);

        // 取载体对象是自愿的逃生舱（ADR-0019）。能转型成功，说明绑定程序集的身份也走共享层。
        var seen = context.Services.GetService(typeof(ICarrierInfo)) is ICarrierInfo carrier
            ? carrier.Describe()
            : "取不到";

        File.WriteAllText(Path.Combine(context.DataDirectory, "carrier.txt"), seen);

        // 探针：Foo 这边的 Lib 是哪一份（ADR-0026）。
        var lib = typeof(Lib.Marker).Assembly;
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "lib.txt"),
            $"{lib.Location} | {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lib)?.Name}");

        // 配置按「包内默认 → 包外覆盖」叠好交过来（ADR-0018）。
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "greeting.txt"),
            context.Configuration["greeting"] ?? "（没有）");

        // 一个 handler 故意抛，另一个照常收到 —— 隔离到 handler（ADR-0014）。
        context.Events.Subscribe<string>(_ => throw new InvalidOperationException("故意抛出"));
        context.Events.Subscribe<string>(payload =>
            File.WriteAllText(Path.Combine(context.DataDirectory, "heard.txt"), payload));

        // 调度器跑在线程池上（ADR-0007）。
        context.Scheduler.RunLater(TimeSpan.FromMilliseconds(50), () =>
            File.WriteAllText(Path.Combine(context.DataDirectory, "scheduled.txt"), "ran"));

        // 这两个句柄**故意不保存、不退订**：框架应当在卸载时自己收回（ADR-0004）。
    }

    public void OnEnable() => Console.WriteLine("[Foo] OnEnable");

    public void OnDisable() => Console.WriteLine("[Foo] OnDisable");

    public void OnUnload() => Console.WriteLine("[Foo] OnUnload");
}
