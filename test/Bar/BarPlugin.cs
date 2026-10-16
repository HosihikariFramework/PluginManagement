using Hosihikari;

[assembly: EntryPoint<Bar.BarPlugin>]

namespace Bar;

public sealed class BarPlugin : IPlugin
{
    public void OnLoad(IPluginContext context)
    {
        Console.WriteLine($"[Bar] OnLoad   package={context.PackageDirectory}");

        // 探针：Bar 这边的 Lib 是哪一份（ADR-0026）。
        var lib = typeof(Lib.Marker).Assembly;
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "lib.txt"),
            $"{lib.Location} | {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lib)?.Name}");
    }

    public void OnEnable() => Console.WriteLine("[Bar] OnEnable");

    public void OnDisable() => Console.WriteLine("[Bar] OnDisable");

    public void OnUnload() => Console.WriteLine("[Bar] OnUnload");
}
