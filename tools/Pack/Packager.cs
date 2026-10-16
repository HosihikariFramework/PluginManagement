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
    /// <summary>
    /// 装配一个包。
    ///
    /// <paramref name="sharedLayer"/> 是目标载体的共享层程序集名清单，与运行期
    /// <c>AssemblyPluginProvider</c> 拿到的那份必须同源（ADR-0021 / 0026）。这里要的是文件名，
    /// 因为打包期只看得见构建输出里的文件名。
    /// </summary>
    public static PackResult Pack(
        string sourceDirectory,
        string manifestPath,
        string outputRoot,
        IReadOnlyList<string> sharedLayer)
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
        // 注意它可能是**传递**来的：作者只直接引用契约，而契约又牵进别的共享层程序集时
        // 一样会出现在构建输出里，所以这里对整份输出逐文件比对，不看是谁带进来的。
        var offenders = Directory
            .EnumerateFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(file => sharedLayer.Contains(file))
            .ToArray();
        if (offenders.Length > 0)
        {
            problems.Add(
                $"构建输出里带了共享层程序集（{string.Join(", ", offenders)}）—— " +
                "引用它们时要 ExcludeAssets=\"runtime\"，否则插件包里是第二个类型身份");
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

    /// <summary>
    /// 只读元数据，不把入口装进任何可执行上下文。
    ///
    /// `Assembly.LoadFrom` 做不到这一点：它把程序集装进**默认 ALC** 并且从此留在那里，
    /// 而在同一个进程里再测插件行为时就被它污染了 —— 插件 ALC 的解析在
    /// `PreferSharedTypes` 下会优先采纳"默认上下文里已经有了的那份"。
    /// </summary>
    private static bool DeclaresEntryPoint(string entryPath, List<string> problems)
    {
        try
        {
            var full = Path.GetFullPath(entryPath);
            var references = Directory.EnumerateFiles(Path.GetDirectoryName(full)!, "*.dll")
                // 契约不在构建输出里（复制进去会被这条规则自己拦下），所以显式补上。
                .Append(typeof(PluginContract).Assembly.Location)
                .Concat(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator));

            using var metadata = new MetadataLoadContext(new PathAssemblyResolver(references));
            var inspection = metadata.LoadFromAssemblyPath(full);

            var declared = inspection.GetCustomAttributesData().Count(IsEntryPoint);
            if (declared != 1)
            {
                problems.Add($"{Path.GetFileName(entryPath)} 声明了 {declared} 个入口 —— 需要恰好一个 [assembly: EntryPoint<T>]");
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

    /// <summary>按名字认，因为在只读元数据里契约是另一套类型对象。</summary>
    private static bool IsEntryPoint(CustomAttributeData attribute)
    {
        for (var type = attribute.AttributeType; type is not null; type = type.BaseType)
        {
            if (type.FullName == typeof(EntryPointAttributeBase).FullName)
            {
                return true;
            }
        }

        return false;
    }
}
