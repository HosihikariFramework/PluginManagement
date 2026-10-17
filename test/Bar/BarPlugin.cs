using Hosihikari;

[assembly: EntryPoint<Bar.BarPlugin>]

namespace Bar;

public sealed class BarPlugin : IPlugin
{
    public void OnLoad(IPluginContext context)
    {
        Console.WriteLine($"[Bar] OnLoad   package={context.PackageDirectory}");

        // 取别的插件登记的服务：键是共享层里的类型，所以取得到 Foo 的实例（ADR-0027）。
        var peer = context.PluginServices.Get<IPlugin>();

        // 再登记一个同类型的服务：应该被拒，先来的那个留着（ADR-0009）。
        var conflicted = context.PluginServices.Register<IPlugin>(this);
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "peer.txt"),
            $"{peer?.GetType().FullName ?? "（取不到）"} | 冲突登记={conflicted}");

        // 探针：Bar 这边的 Lib 是哪一份（ADR-0026）。
        var lib = typeof(Lib.Marker).Assembly;
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "lib.txt"),
            $"{lib.Location} | {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lib)?.Name}");
    }

    public PluginHookResult OnEnable()
    {
        Console.WriteLine("[Bar] OnEnable");
        return PluginHookResult.Allow();
    }

    public PluginHookResult OnDisable()
    {
        Console.WriteLine("[Bar] OnDisable");
        return PluginHookResult.Allow();
    }

    public void OnUnload() => Console.WriteLine("[Bar] OnUnload");
}
