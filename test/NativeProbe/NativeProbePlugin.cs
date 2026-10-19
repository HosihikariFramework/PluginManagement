using System.Runtime.InteropServices;

using Hosihikari;

[assembly: EntryPoint<NativeProbe.NativeProbePlugin>]

namespace NativeProbe;

public sealed class NativeProbePlugin : IPlugin
{
    public void OnLoad(IPluginContext context)
    {
        var value = Probe.Value();
        File.WriteAllText(Path.Combine(context.DataDirectory, "native.txt"), value.ToString());
    }

    public PluginHookResult OnEnable() => PluginHookResult.Allow();

    public PluginHookResult OnDisable() => PluginHookResult.Allow();

    public void OnUnload()
    {
    }
}

internal static class Probe
{
    [DllImport("hosihikari_probe", EntryPoint = "hosihikari_probe")]
    public static extern int Value();
}
