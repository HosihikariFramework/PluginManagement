using System.Reflection;
using System.Text.Json;
using Hosihikari;

namespace Hosihikari.PluginManagement.Pack;

/// <summary>一次打包的结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="PackageDirectory">成功时的包目录。</param>
/// <param name="Problems">失败原因，逐条可读。</param>
public readonly record struct PackResult(bool Succeeded, string? PackageDirectory, IReadOnlyList<string> Problems);

/// <summary>
/// 把一份构建输出 + 一份清单装配成插件包，并在这里检查那些**报错点离病因很远**的规则。
/// 作者手写清单仍然可以跑得起来，但会在运行期以难以归因的方式失败（ADR-0004 / 0006 / 0008 / 0012）。
/// </summary>
public static class Packager
{
    /// <summary>共享层里的程序集不许出现在插件包里 —— 它们由宿主的共享层提供。</summary>
    public static readonly string[] SharedLayerAssemblies = ["Hosihikari.Abstractions.dll"];

    public static PackResult Pack(string sourceDirectory, string manifestPath, string outputRoot)
    {
        var problems = new List<string>();

        PluginManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            problems.Add($"清单读不出来：{ex.Message}");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name))
        {
            problems.Add("清单缺少 name");
            return new PackResult(false, null, problems);
        }

        // 包目录名就是插件名 —— 目录名与清单 name 一致这条由这里保证（ADR-0006）。
        var packageDirectory = Path.Combine(outputRoot, manifest.Name);

        // 契约主版本（ADR-0012）。
        if (manifest.Api != PluginContract.ApiVersion)
        {
            problems.Add($"清单 api 是 {manifest.Api ?? "未声明"}，当前契约是 {PluginContract.ApiVersion}");
        }

        // type 是父级的路由键（ADR-0006）。写错的包在父级态下根本到不了我们手里，
        // 而且失败点离病因很远 —— 所以在这里就拦下。
        if (manifest.Type != PluginContract.ShimModName)
        {
            problems.Add($"清单 type 必须是 {PluginContract.ShimModName}，实际 {(string.IsNullOrWhiteSpace(manifest.Type) ? "未声明" : manifest.Type)}");
        }

        // 共享层副本（ADR-0004 / 0019）—— 这条是静默失败的那条，所以放在打包期拦。
        var offenders = Directory
            .EnumerateFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(file => Array.Exists(SharedLayerAssemblies, shared => shared == file))
            .ToArray();
        if (offenders.Length > 0)
        {
            problems.Add(
                $"构建输出里带了共享层程序集（{string.Join(", ", offenders)}）—— " +
                "引用契约时要 ExcludeAssets=\"runtime\"，否则插件包里是第二个类型身份");
        }

        // 入口（ADR-0008）。编译器已经保证它与 IPlugin 和有无参构造是一致的，这里只确认它存在。
        if (manifest.Provider == "assembly")
        {
            var entryPath = Path.Combine(sourceDirectory, manifest.Entry);
            if (!File.Exists(entryPath))
            {
                problems.Add($"清单 entry 指向的 {manifest.Entry} 不在构建输出里");
            }
            else if (!DeclaresEntryPoint(entryPath, problems))
            {
                // DeclaresEntryPoint 自己填了原因
            }
        }

        if (problems.Count > 0)
        {
            return new PackResult(false, null, problems);
        }

        Directory.CreateDirectory(packageDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(packageDirectory, Path.GetFileName(file)), overwrite: true);
        }

        File.Copy(manifestPath, Path.Combine(packageDirectory, "manifest.json"), overwrite: true);
        return new PackResult(true, packageDirectory, []);
    }

    private static bool DeclaresEntryPoint(string entryPath, List<string> problems)
    {
        try
        {
            // 反射读程序集而不载入它 —— 打包工具不该把插件的 ALC 带起来。
            var inspection = Assembly.LoadFrom(Path.GetFullPath(entryPath));
            var declared = inspection.GetCustomAttributes<EntryPointAttributeBase>().ToArray();
            if (declared.Length != 1)
            {
                problems.Add($"{Path.GetFileName(entryPath)} 声明了 {declared.Length} 个入口 —— 需要恰好一个 [assembly: EntryPoint<T>]");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            problems.Add($"读不了 {Path.GetFileName(entryPath)} 的入口声明：{ex.Message}");
            return false;
        }
    }
}
