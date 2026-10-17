using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Hosihikari;
using Hosihikari.Bindings;
using Hosihikari.PluginManagement;
using Hosihikari.PluginManagement.Pack;
using Hosihikari.PluginManagement.Spike;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台主机驱动核心，不碰 BDS、Shim、任何载体。
var repoRoot = RepoRoot();
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
var configRoot = Path.Combine(stage, "config", "hosihikari");
var reported = new List<string>();

// 目标载体的共享层清单。打包工具与运行期必须拿同一份 —— 一个来自这里，另一个来自
// AssemblyPluginProvider 的构造（ADR-0021 / 0026）。这个宿主用的是替身载体绑定。
var sharedLayer = new[] { "Hosihikari.Abstractions.dll", "Hosihikari.Bindings.Stub.dll" };

Stage("Foo", withConfig: true);
Stage("Bar", withConfig: false);
StageBroken();
StageScript();
StagePinned();

// 宿主服务用现成的包拼装（ADR-0007）：DI + 日志。载体对象经服务解析交给插件（ADR-0019）。
var collection = new ServiceCollection();
collection.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));
collection.AddSingleton(typeof(ICarrierInfo), new StubCarrier("stub"));
var services = collection.BuildServiceProvider();

// 运营方的覆盖放到包外（ADR-0018）。
Directory.CreateDirectory(configRoot);
File.WriteAllText(Path.Combine(configRoot, "Foo.conf"), "greeting = \"overridden\"\n");

var host = new PluginManagerHost(
    pluginsRoot,
    dataRoot,
    configRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services);
var pinnedProvider = new PinnedPluginProvider();
host.AddProvider(new AssemblyPluginProvider(typeof(StubCarrier).Assembly));
host.AddProvider(new ScriptPluginProvider());
host.AddProvider(pinnedProvider);

Console.WriteLine("=== 加载 ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

// 事件与调度是异步生效的，等一小会儿再看结果。
host.Events.Publish("ping");
Thread.Sleep(300);

var checks = new List<(string Name, bool Passed, string Detail)>();

// A9：一个 handler 抛出，其余照常收到（ADR-0014）
var heardFile = Path.Combine(dataRoot, "Foo", "heard.txt");
var heard = File.Exists(heardFile) ? File.ReadAllText(heardFile) : "（没收到）";
checks.Add(("A9 handler 抛出不影响其余 handler", heard == "ping", heard));

// A10：调度器的一次性任务真的跑了（ADR-0007）
var scheduledFile = Path.Combine(dataRoot, "Foo", "scheduled.txt");
checks.Add(("A10 调度器的一次性任务执行", File.Exists(scheduledFile), File.Exists(scheduledFile) ? "已执行" : "没跑"));

// A16：普通依赖（非契约、不在共享层）按 ALC 各一份 —— 这正是可卸载性的代价，
// 也是"插件之间不共享类型"的另一面（ADR-0026）。
var fooLib = File.ReadAllText(Path.Combine(dataRoot, "Foo", "lib.txt"));
var barLib = File.ReadAllText(Path.Combine(dataRoot, "Bar", "lib.txt"));
checks.Add((
    "A16 普通依赖按 ALC 各一份",
    fooLib != barLib,
    $"Foo: {fooLib} ／ Bar: {barLib}"
));

// A11：两个测试插件都经打包工具装配成了包
var packedOk = Directory.Exists(Path.Combine(pluginsRoot, "Foo")) && Directory.Exists(Path.Combine(pluginsRoot, "Bar"));
checks.Add(("A11 打包工具产出包", packedOk, packedOk ? "Foo / Bar" : "缺包"));

// A12：误把契约打进包里的构建输出，打包必须拦下（ADR-0004 / 0019）—— 这条在运行期是静默的
var badSource = Path.Combine(stage, "bad-source");
Directory.CreateDirectory(badSource);
File.Copy(
    Path.Combine(AppContext.BaseDirectory, "Hosihikari.Abstractions.dll"),
    Path.Combine(badSource, "Hosihikari.Abstractions.dll"),
    overwrite: true);
var bad = Packager.Pack(badSource, Path.Combine(repoRoot, "test", "Foo", "manifest.json"), Path.Combine(stage, "bad-out"), sharedLayer);
var sharedProblem = bad.Problems.FirstOrDefault(problem => problem.Contains("共享层"));
checks.Add(("A12 打包拒绝共享层副本", !bad.Succeeded && sharedProblem is not null, sharedProblem ?? "（未拦下）"));

// A6：api 不匹配必须被拒载并留下可读报告（ADR-0012）
var rejected = !host.LoadedNames.Contains("Broken") && reported.Any(entry => entry.Contains("api 不匹配"));
checks.Add(("A6 api 不匹配时拒载", rejected, reported.LastOrDefault(entry => entry.Contains("api")) ?? "（无报告）"));

// A5：载体绑定程序集也走共享层 —— 转型成功才拿得到，副本会拿到另一种身份（ADR-0019）
var carrierFile = Path.Combine(dataRoot, "Foo", "carrier.txt");
var seen = File.Exists(carrierFile) ? File.ReadAllText(carrierFile) : "（插件没写出东西）";
checks.Add(("A5 载体绑定也走共享层", seen == "stub-carrier:stub", seen));

// A8：包内默认被包外覆盖，且没写配置的插件照样加载（ADR-0018）
var greetingFile = Path.Combine(dataRoot, "Foo", "greeting.txt");
var greeting = File.Exists(greetingFile) ? File.ReadAllText(greetingFile) : "（插件没写出东西）";
checks.Add(("A8 配置分层：包外覆盖包内", greeting == "overridden" && host.LoadedNames.Contains("Bar"), greeting));

// A7：另一种插件类型（不碰 ALC）走的是同一套契约与同一套生命周期（ADR-0002 / ADR-0010）
var ranFile = Path.Combine(dataRoot, "Lua", "ran.txt");
checks.Add(("A7 非 ALC 的插件类型走同一契约", File.Exists(ranFile), File.Exists(ranFile) ? "脚本已执行" : "脚本没跑"));

// A4：Bar 依赖 Foo，此时卸 Foo 必须被拒，且如实返回（ADR-0004）
var refused = host.Unload("Foo");
checks.Add(("A4 有依赖者时拒绝卸载", !refused.Released && !string.IsNullOrEmpty(refused.Error), refused.Error ?? "（未拒绝）"));

// A13：父级态下父级把包逐个交过来（ADR-0003）—— 这个包不在 plugins/ 下，只有交付能让它进来
var delivered = host.Load(Path.Combine(stage, "parented", "Pinned"));
checks.Add((
    "A13 父级交付的包装上了",
    delivered && host.LoadedNames.Contains("Pinned"),
    delivered ? string.Join(", ", host.LoadedNames) : "交付被拒"
));

// A14：放下之后回收验证没过，就是没卸掉 —— 照实报失败（ADR-0004）
var pinnedFirst = host.Unload("Pinned");
checks.Add((
    "A14 回收没过时如实报失败",
    !pinnedFirst.Released && pinnedFirst.Error?.Contains("回收") == true,
    pinnedFirst.Error ?? "（居然说卸掉了）"
));

// A15：再问一次是重新验证，不是"未加载"；外面那根没了就真卸掉
pinnedProvider.Pinned = false;
var pinnedSecond = host.Unload("Pinned");
checks.Add((
    "A15 待回收的插件可以再卸一次",
    pinnedSecond.Released && !host.LoadedNames.Contains("Pinned"),
    pinnedSecond.Error ?? "已回收"
));

// A1：每个插件一个 collectible ALC（ADR-0004）
var collectible = CollectibleContextCount();
checks.Add(("A1 每插件一个 collectible ALC", collectible >= 2, $"进程内 collectible ALC 数 = {collectible}"));

// A2：插件包内不得有共享层副本 —— 这是会静默失败的那条规则（ADR-0004 / ADR-0007 / ADR-0019）。
// 清单用的是给打包工具的那一份，不再另写一份：同一份事实源（ADR-0026）。
var copies = Directory.EnumerateFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories)
    .Where(file => sharedLayer.Contains(Path.GetFileName(file)))
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

// 测试插件走打包工具 —— 它们就是它的前两个用户，包因此与作者拿到的是同一种东西。
void Stage(string name, bool withConfig)
{
    var manifestPath = Path.Combine(repoRoot, "test", name, "manifest.json");
    var packed = Packager.Pack(OutputDirectory(name), manifestPath, pluginsRoot, sharedLayer);
    if (!packed.Succeeded)
    {
        reported.Add($"{name}: 打包失败 —— {string.Join("；", packed.Problems)}");
        return;
    }

    if (withConfig)
    {
        // 作者给的默认值，随包更新（ADR-0018）。
        File.WriteAllText(Path.Combine(packed.PackageDirectory!, $"{name}.conf"), "greeting = \"default\"\n");
    }
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
          "type": "hosihikari",
          "extraInfo": { "api": "999", "format_version": "1" }
        }
        """);
}

// 另一种插件类型的包：入口是脚本文件，清单与程序集型同形 —— 入口怎么找是各自 Provider 的事。
void StageScript()
{
    var package = Path.Combine(pluginsRoot, "Lua");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "main.lua"), "print('hello from script')\n");
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Lua",
          "entry": "main.lua",
          "type": "hosihikari",
          "extraInfo": { "api": "1", "format_version": "1", "provider": "script" }
        }
        """);
}

// 父级态的那个包：故意放在 plugins/ **外面** —— 只有父级交付能让它进来（ADR-0003）。
void StagePinned()
{
    var package = Path.Combine(stage, "parented", "Pinned");
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Pinned",
          "entry": "Pinned.dll",
          "type": "hosihikari",
          "extraInfo": { "api": "1", "format_version": "1", "provider": "pinned" }
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
