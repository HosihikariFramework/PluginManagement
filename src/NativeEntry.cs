using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginManagement;

/// <summary>
/// 原生 Shim 调进来的那一个入口（ADR-0005）—— "原生 → 托管"这条缝的托管一侧。
/// 它由 <c>get_function_pointer</c> 按类型名与方法名取到，形状就是 <c>component_entry_point_fn</c>。
/// </summary>
public static class NativeEntry
{
    // 插件活得比这个方法长，所以宿主与日志工厂也必须活得比它长。
    private static PluginManagerHost? s_host;
    private static ILoggerFactory? s_loggerFactory;

    /// <summary>
    /// 启动核心并把插件装起来。返回 0 表示成功，非 0 是错误码。
    ///
    /// **不抛异常**：在别人的进程里，托管异常越过这条边界等于把宿主一起拖下水（ADR-0009）。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int Start(IntPtr arg, int argSizeInBytes)
    {
        s_loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));
        var logger = s_loggerFactory.CreateLogger("Hosihikari");

        try
        {
            // 服务端根就是进程的工作目录；插件、配置与数据都相对它找（ADR-0018）。
            var root = Directory.GetCurrentDirectory();
            s_host = new PluginManagerHost(
                Path.Combine(root, "plugins"),
                Path.Combine(root, "data", "hosihikari"),
                Path.Combine(root, "config", "hosihikari"),
                entry => logger.LogError("加载问题：{Entry}", entry));
            s_host.AddProvider(new AssemblyPluginProvider());

            s_host.LoadAll();
            logger.LogInformation(
                "已加载 {Count} 个插件：[{Names}]",
                s_host.LoadedNames.Count,
                string.Join(", ", s_host.LoadedNames));
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动失败");
            return 1;
        }
    }
}
