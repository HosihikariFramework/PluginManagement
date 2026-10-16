using Hosihikari;

namespace Hosihikari.PluginManagement.Spike;

/// <summary>
/// ADR-0002 的检验：一个**完全不碰 ALC** 的插件类型，用的还是同一套 <see cref="IPluginProvider"/>。
/// 真实的脚本 Provider 归 ScriptManager 仓，这里只为证明核心对类型是真中立的。
/// </summary>
public sealed class ScriptPluginProvider : IPluginProvider
{
    public string Type => "script";

    public bool CanLoad(PluginManifest manifest) => manifest.Provider == Type;

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var scriptPath = Path.Combine(packageDirectory, manifest.Entry);
        if (!File.Exists(scriptPath))
        {
            return null;
        }

        return new ScriptPluginInstance(File.ReadAllText(scriptPath), context.DataDirectory);
    }
}

internal sealed class ScriptPluginInstance(string source, string dataDirectory)
    : PluginInstance(new ScriptPlugin(source, dataDirectory))
{
    /// <summary>没有 ALC 要卸，验证器恒为真 —— 释放句柄长什么样，由 Provider 自己决定（ADR-0010）。</summary>
    public override Func<bool> Release() => static () => true;
}

internal sealed class ScriptPlugin(string source, string dataDirectory) : IPlugin
{
    public void OnLoad(IPluginContext context) => Console.WriteLine($"[Lua] OnLoad   {source.Trim().Length} 字符的脚本");

    public void OnEnable()
    {
        Console.WriteLine("[Lua] OnEnable");
        File.WriteAllText(Path.Combine(dataDirectory, "ran.txt"), source.Trim());
    }

    public void OnDisable() => Console.WriteLine("[Lua] OnDisable");

    public void OnUnload() => Console.WriteLine("[Lua] OnUnload");
}
