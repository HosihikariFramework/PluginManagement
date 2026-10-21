using Hosihikari;
using NuGet.Versioning;

namespace Hosihikari.PluginManagement;

/// <summary>清单版本与依赖约束的解析（ADR-0039）。</summary>
internal static class DependencyVersion
{
    /// <summary>校验清单里的版本与每条依赖约束的语法；缺失版本按“任意版本”处理。</summary>
    internal static bool TryValidateManifest(PluginManifest manifest, out string? problem)
    {
        problem = null;

        if (!string.IsNullOrWhiteSpace(manifest.Version)
            && !NuGetVersion.TryParse(manifest.Version, out _))
        {
            problem = $"{manifest.Name}: 清单 version 不是合法 SemVer：{manifest.Version}";
            return false;
        }

        foreach (var dependency in manifest.Dependencies)
        {
            if (!string.IsNullOrWhiteSpace(dependency.Version)
                && !VersionRange.TryParse(dependency.Version, out _))
            {
                problem = $"{manifest.Name}: 依赖 {dependency.Name} 的版本约束不是合法 SemVer 范围：{dependency.Version}";
                return false;
            }
        }

        return true;
    }

    /// <summary>目标插件的版本是否满足这条依赖约束；没有约束时接受任意版本。</summary>
    internal static bool Satisfies(PluginManifest target, ManifestDependency dependency, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(dependency.Version) || dependency.Version.Trim() == "*")
        {
            return true;
        }

        if (!VersionRange.TryParse(dependency.Version, out var range))
        {
            problem = $"依赖 {dependency.Name} 的版本约束无效：{dependency.Version}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(target.Version)
            || !NuGetVersion.TryParse(target.Version, out var version))
        {
            problem = $"依赖 {dependency.Name} 没有可比较的 version，无法满足 {dependency.Version}";
            return false;
        }

        if (!range.Satisfies(version))
        {
            problem = $"依赖 {dependency.Name} 的版本 {version} 不满足 {dependency.Version}";
            return false;
        }

        return true;
    }
}
