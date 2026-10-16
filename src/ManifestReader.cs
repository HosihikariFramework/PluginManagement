using System.Text.Json;

namespace Hosihikari.PluginManagement;

/// <summary>扫 <c>plugins/</c> 下每个目录的 <c>manifest.json</c>（ADR-0006）。</summary>
internal static class ManifestReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var folderName = Path.GetFileName(directory);
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options);
                if (manifest is null)
                {
                    report($"{folderName}: 清单解析为空");
                    continue;
                }

                // LeviLamina 也要求这两条，我们跟着校验，好让三个拓扑同一个包同一种失败（ADR-0006）。
                if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name != folderName)
                {
                    report($"{folderName}: 清单 name 必须等于目录名");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Type) || string.IsNullOrWhiteSpace(manifest.Entry))
                {
                    report($"{folderName}: 清单缺少 type 或 entry");
                    continue;
                }

                if (manifest.Api != PluginContract.ApiVersion)
                {
                    report($"{folderName}: api 不匹配（清单 {manifest.Api ?? "未声明"}，框架 {PluginContract.ApiVersion}）");
                    continue;
                }

                packages.Add(new PluginPackage(manifest, directory));
            }
            catch (JsonException ex)
            {
                report($"{folderName}: 清单不是合法 JSON —— {ex.Message}");
            }
            catch (IOException ex)
            {
                report($"{folderName}: 清单读不出来 —— {ex.Message}");
            }
        }

        return packages;
    }
}

internal sealed record PluginPackage(PluginManifest Manifest, string Directory);
