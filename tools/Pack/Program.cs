using Hosihikari.PluginManagement.Pack;

if (args.Length < 3)
{
    Console.WriteLine("用法: Hosihikari.Plugin.Pack <构建输出目录> <清单路径> <输出根目录>");
    return 2;
}

var result = Packager.Pack(args[0], args[1], args[2]);
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
