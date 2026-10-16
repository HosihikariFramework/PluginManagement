using Hosihikari;

namespace Bar;

public sealed class BarPlugin : IPlugin
{
    public void OnLoad(IPluginContext context) => Console.WriteLine($"[Bar] OnLoad   package={context.PackageDirectory}");

    public void OnEnable() => Console.WriteLine("[Bar] OnEnable");

    public void OnDisable() => Console.WriteLine("[Bar] OnDisable");

    public void OnUnload() => Console.WriteLine("[Bar] OnUnload");
}
