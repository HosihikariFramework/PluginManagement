using System.Text.Json;

namespace Hosihikari.PluginManagement;

/// <summary>读插件包目录里的 <c>manifest.json</c>（ADR-0006）。</summary>
internal static class ManifestReader
{
    private const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>自持态用：扫 <c>plugins/</c> 下的每个目录。</summary>
    internal static List<PluginPackage> Scan(string pluginsRoot, Action<string> report)
    {
        var packages = new List<PluginPackage>();

        // 目录不存在属于正常情况，不是错误（ADR-0009）。
        if (!Directory.Exists(pluginsRoot))
        {
            return packages;
        }

        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
        {
            // 不是包目录也属正常：父级自己的插件就住在同一个 plugins/ 下（ADR-0003），
            // 所以这里对没有清单的目录不吭声。
            if (File.Exists(Path.Combine(directory, FileName)) && Read(directory, report) is { } package)
            {
                packages.Add(package);
            }
        }

        return packages;
    }

    /// <summary>父级态用：父级逐个交付，清单只有一个读者（ADR-0003）。目录读不出来时返回 null。</summary>
    internal static PluginPackage? Read(string directory, Action<string> report)
    {
        var folderName = Path.GetFileName(directory);
        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
        {
            report($"{folderName}: 目录里没有 {FileName}");
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options);
            if (manifest is null)
            {
                report($"{folderName}: 清单解析为空");
                return null;
            }

            // LeviLamina 也要求这两条，我们跟着校验，好让三个拓扑同一个包同一种失败（ADR-0006）。
            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name != folderName)
            {
                report($"{folderName}: 清单 name 必须等于目录名");
                return null;
            }

            if (string.IsNullOrWhiteSpace(manifest.Entry))
            {
                report($"{folderName}: 清单缺少 entry");
                return null;
            }

            // type 是父级的路由键。写错的包不会被父级交过来，所以在自持态下也一并拦下 ——
            // 否则同一个包换一个拓扑才失败，而那是最难查的一类（ADR-0012）。
            if (manifest.Type != PluginContract.ShimModName)
            {
                var actual = string.IsNullOrWhiteSpace(manifest.Type) ? "未声明" : manifest.Type;
                report($"{folderName}: 清单 type 必须是 {PluginContract.ShimModName}，实际 {actual}");
                return null;
            }

            if (manifest.Api != PluginContract.ApiVersion)
            {
                report($"{folderName}: api 不匹配（清单 {manifest.Api ?? "未声明"}，框架 {PluginContract.ApiVersion}）");
                return null;
            }

            return new PluginPackage(manifest, directory);
        }
        catch (JsonException ex)
        {
            report($"{folderName}: 清单不是合法 JSON —— {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            report($"{folderName}: 清单读不出来 —— {ex.Message}");
            return null;
        }
    }
}

internal sealed record PluginPackage(PluginManifest Manifest, string Directory);
