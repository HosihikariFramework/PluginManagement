using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Hosihikari;
using Hosihikari.Bindings;
using Hosihikari.PluginManagement;
using Hosihikari.PluginManagement.Spike;

// 载体中立检查（ADR-0015）：普通 .NET 控制台主机驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
Stage("Foo", []);
Stage("Bar", ["Foo"]);
StageBroken();
StageScript();

// 载体对象经服务解析交给插件，取用是自愿的逃生舱（ADR-0007 / ADR-0019）。
var services = new SimpleServices();
services.Add(typeof(ICarrierInfo), new StubCarrier("stub"));

var reported = new List<string>();
var host = new PluginManagerHost(
    pluginsRoot,
    dataRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services);
host.AddProvider(new AssemblyPluginProvider(typeof(StubCarrier).Assembly));
host.AddProvider(new ScriptPluginProvider());

Console.WriteLine("=== 加载 ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

var checks = new List<(string Name, bool Passed, string Detail)>();

// A6：api 不匹配必须被拒载并留下可读报告（ADR-0012）
var rejected = !host.LoadedNames.Contains("Broken") && reported.Any(entry => entry.Contains("api 不匹配"));
checks.Add(("A6 api 不匹配时拒载", rejected, reported.LastOrDefault(entry => entry.Contains("api")) ?? "（无报告）"));

// A5：载体绑定程序集也走共享层 —— 转型成功才拿得到，副本会拿到另一种身份（ADR-0019）
var carrierFile = Path.Combine(dataRoot, "Foo", "carrier.txt");
var seen = File.Exists(carrierFile) ? File.ReadAllText(carrierFile) : "（插件没写出东西）";
checks.Add(("A5 载体绑定也走共享层", seen == "stub-carrier:stub", seen));

// A7：另一种插件类型（不碰 ALC）走的是同一套契约与同一套生命周期（ADR-0002 / ADR-0010）
var ranFile = Path.Combine(dataRoot, "Lua", "ran.txt");
checks.Add(("A7 非 ALC 的插件类型走同一契约", File.Exists(ranFile), File.Exists(ranFile) ? "脚本已执行" : "脚本没跑"));

// A4：Bar 依赖 Foo，此时卸 Foo 必须被拒，且如实返回（ADR-0004）
var refused = host.Unload("Foo");
checks.Add(("A4 有依赖者时拒绝卸载", !refused.Released && !string.IsNullOrEmpty(refused.Error), refused.Error ?? "（未拒绝）"));

// A1：每个插件一个 collectible ALC（ADR-0004）
var collectible = CollectibleContextCount();
checks.Add(("A1 每插件一个 collectible ALC", collectible >= 2, $"进程内 collectible ALC 数 = {collectible}"));

// A2：插件包内不得有共享层副本 —— 这是会静默失败的那条规则（ADR-0004 / ADR-0019）
var copies = Directory.EnumerateFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories)
    .Where(file => Path.GetFileName(file) is "Hosihikari.Abstractions.dll" or "Hosihikari.Bindings.Stub.dll")
    .ToArray();
checks.Add(("A2 插件包内无共享层副本", copies.Length == 0, copies.Length == 0 ? "无副本" : string.Join(", ", copies)));

// A3：卸载后真回收，而不是"停用就算卸了"（ADR-0004）
var bar = host.Unload("Bar");
checks.Add(("A3 卸载后 ALC 真回收", bar.Released, bar.Error ?? "已回收"));

// 依赖者卸掉之后，Foo 应可卸
var foo = host.Unload("Foo");
checks.Add(("   依赖者卸掉后 Foo 可卸", foo.Released, foo.Error ?? "已回收"));

// 脚本插件没有依赖者，随时可卸
var lua = host.Unload("Lua");
checks.Add(("   脚本插件可卸", lua.Released, lua.Error ?? "已回收"));

// 收尾自检：全部卸载后不应再有任何 collectible ALC（ADR-0004）。
GC.Collect();
GC.WaitForPendingFinalizers();
Console.WriteLine();
Console.WriteLine($"[诊断] 全部卸载后残留 collectible ALC = {CollectibleContextCount()}");

Console.WriteLine();
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

void Stage(string name, string[] dependencies)
{
    var package = Path.Combine(pluginsRoot, name);
    Directory.CreateDirectory(package);

    // 整份构建输出进包，与真实插件包的形态一致 —— 共享层副本会不会混进来，由构建规则决定。
    foreach (var file in Directory.EnumerateFiles(OutputDirectory(name), "*", SearchOption.TopDirectoryOnly))
    {
        File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
    }

    var deps = string.Join(", ", dependencies.Select(dependency => $$"""{ "name": "{{dependency}}" }"""));
    File.WriteAllText(Path.Combine(package, "manifest.json"), $$"""
        {
          "name": "{{name}}",
          "entry": "{{name}}.dll",
          "type": "assembly",
          "version": "1.0.0",
          "dependencies": [{{deps}}],
          "extraInfo": {
            "api": "{{PluginContract.ApiVersion}}",
            "format_version": "1",
            "entryType": "{{name}}.{{name}}Plugin"
          }
        }
        """);
}

// 清单合法但没有能兑现的 api —— 测拒载路径（ADR-0012）。入口文件故意不存在：拒载要发生在载入之前。
void StageBroken()
{
    var package = Path.Combine(pluginsRoot, "Broken");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), $$"""
        {
          "name": "Broken",
          "entry": "Broken.dll",
          "type": "assembly",
          "extraInfo": { "api": "999", "format_version": "1", "entryType": "Broken.BrokenPlugin" }
        }
        """);
}

// 另一种插件类型的包：入口是脚本文件，清单里没有 entryType（脚本 Provider 不需要它）。
void StageScript()
{
    var package = Path.Combine(pluginsRoot, "Lua");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "main.lua"), "print('hello from script')\n");
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Lua",
          "entry": "main.lua",
          "type": "script",
          "extraInfo": { "api": "1", "format_version": "1" }
        }
        """);
}

// 单独一个方法：枚举 AssemblyLoadContext.All 会把枚举到的 ALC 钉在本方法帧上，
// 留在主流程里会让后面的卸载验证假失败 —— 观察者自己成了根（ADR-0023）。
[MethodImpl(MethodImplOptions.NoInlining)]
static int CollectibleContextCount()
{
    var count = 0;
    foreach (var context in AssemblyLoadContext.All)
    {
        if (context.IsCollectible)
        {
            count++;
        }
    }

    return count;
}

string OutputDirectory(string name)
{
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    var bin = Path.Combine(repoRoot, "test", name, "bin");
    var assembly = Directory.EnumerateFiles(bin, $"{name}.dll", SearchOption.AllDirectories)
        .Where(file => file.Contains($"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}"))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .First();
    return Path.GetDirectoryName(assembly)!;
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Hosihikari.PluginManager.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根");
}

internal sealed class SimpleServices : IServiceProvider
{
    private readonly Dictionary<Type, object> _map = [];

    public void Add(Type type, object instance) => _map[type] = instance;

    public object? GetService(Type serviceType) => _map.GetValueOrDefault(serviceType);
}
