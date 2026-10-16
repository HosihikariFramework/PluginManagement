using Hosihikari;
using Hosihikari.Bindings;

namespace Foo;

public sealed class FooPlugin : IPlugin
{
    public void OnLoad(IPluginContext context)
    {
        Console.WriteLine($"[Foo] OnLoad   package={context.PackageDirectory}");

        // 取载体对象是自愿的逃生舱（ADR-0019）。能转型成功，说明绑定程序集的身份也走共享层。
        var seen = context.Services.GetService(typeof(ICarrierInfo)) is ICarrierInfo carrier
            ? carrier.Describe()
            : "取不到";

        File.WriteAllText(Path.Combine(context.DataDirectory, "carrier.txt"), seen);
    }

    public void OnEnable() => Console.WriteLine("[Foo] OnEnable");

    public void OnDisable() => Console.WriteLine("[Foo] OnDisable");

    public void OnUnload() => Console.WriteLine("[Foo] OnUnload");
}
