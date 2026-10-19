using Hosihikari.PluginManagement;
using Hosihikari.PluginManagement.Pack;

// 用法：
//   Hosihikari.Plugin.Pack pack-provider <构建输出目录> <清单路径> <输出根目录>
//   Hosihikari.Plugin.Pack <构建输出目录> <清单路径> <输出根目录> [--shared <程序集文件名>]...
// pack-provider 用核心的共享层清单剔除共享层副本（ADR-0036）；作者打包仍用后半条显式清单。
if (args.Length == 4 && args[0] == "pack-provider")
{
    return PackProvider(args[1], args[2], args[3]);
}

// --shared 可以重复，写的是**目标载体**的共享层清单 —— 它由载体声明，
// 与运行期 AssemblyPluginProvider 拿到的那份必须同源（ADR-0021 / 0026）。
if (args.Length < 3)
{
    Console.WriteLine("用法:");
    Console.WriteLine("  Hosihikari.Plugin.Pack pack-provider <构建输出目录> <清单路径> <输出根目录>");
    Console.WriteLine("  Hosihikari.Plugin.Pack <构建输出目录> <清单路径> <输出根目录> [--shared <程序集文件名>]...");
    return 2;
}

var sharedLayer = new List<string>();
for (var i = 3; i < args.Length; i++)
{
    if (args[i] != "--shared" || i + 1 >= args.Length)
    {
        Console.WriteLine($"无法识别的参数：{args[i]}");
        return 2;
    }

    sharedLayer.Add(args[++i]);
}

if (sharedLayer.Count == 0)
{
    Console.WriteLine("至少要给一个 --shared：共享层清单写不全会让副本漏过这道检查，而它是静默失败的那类问题。");
    return 2;
}

var result = Packager.Pack(args[0], args[1], args[2], sharedLayer);
if (result.Succeeded)
{
    Console.WriteLine($"已打包：{result.PackageDirectory}");
    return 0;
}

Console.WriteLine("打包失败：");
foreach (var problem in result.Problems)
{
    Console.WriteLine($"- {problem}");
}

return 1;

// Provider 包的装配：先按核心的共享层清单剔除副本，再交给 Packager 做常规校验与复制。
// 共享清单只有这一份（AssemblyPluginProvider.SharedAssemblyFileNames），三个 pack.ps1 不再各写一份。
static int PackProvider(string sourceDirectory, string manifestPath, string outputRoot)
{
    var shared = AssemblyPluginProvider.SharedAssemblyFileNames;
    var sharedBases = shared
        .Select(name => Path.GetFileNameWithoutExtension(name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var staging = Path.Combine(Path.GetTempPath(), "hosihikari-pack-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);
    try
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (sharedBases.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        var result = Packager.Pack(staging, manifestPath, outputRoot, shared);
        if (result.Succeeded)
        {
            Console.WriteLine($"已打包：{result.PackageDirectory}");
            return 0;
        }

        Console.WriteLine("打包失败：");
        foreach (var problem in result.Problems)
        {
            Console.WriteLine($"- {problem}");
        }

        return 1;
    }
    finally
    {
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
