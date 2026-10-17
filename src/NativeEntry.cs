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
    /// 自持态入口：插件包就在 <c>plugins/</c> 下，由我们枚举装载（ADR-0018）。
    ///
    /// **不抛异常**：在别人的进程里，托管异常越过这条边界等于把宿主一起拖下水（ADR-0009）。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int Start(IntPtr arg, int argSizeInBytes) => StartCore(scanPlugins: true);

    /// <summary>
    /// 父级态入口：插件集归父级注册表所有（ADR-0003），我们等父级把插件交过来。
    /// 枚举 <c>plugins/</c> 会把父级自己的模组一并收编，所以这里不扫目录。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int StartUnderParent(IntPtr arg, int argSizeInBytes) => StartCore(scanPlugins: false);

    /// <summary>
    /// 父级交付一个插件包：参数是包目录的、以 null 结尾的 UTF-8 指针（ADR-0003）。
    /// 返回 0 表示装上了，非 0 表示没装上 —— 原因已经进了父级的日志。
    /// </summary>
    /// <param name="arg">包目录的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int LoadPlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Load);

    /// <summary>
    /// 父级收回一个插件：参数是插件名的、以 null 结尾的 UTF-8 指针（ADR-0003）。
    /// 返回 0 表示回收验证过了；非 0 表示放下了但还没回收，父级可以再问一次。
    /// </summary>
    /// <param name="arg">插件名的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int UnloadPlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Unload);

    /// <summary>父级单独启停一个子插件（ADR-0003）：参数是插件名的 UTF-8 指针。</summary>
    /// <param name="arg">插件名的 UTF-8 指针。</param>
    /// <param name="argSizeInBytes">那块缓冲的字节数，含结尾的 null。</param>
    [UnmanagedCallersOnly]
    public static int EnablePlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Enable);

    /// <inheritdoc cref="EnablePlugin"/>
    [UnmanagedCallersOnly]
    public static int DisablePlugin(IntPtr arg, int argSizeInBytes) => Deliver(arg, Verb.Disable);

    /// <summary>父级能让我们做的四件事。它驱动的是同一个入口，只是动作不同。</summary>
    private enum Verb
    {
        Load,
        Unload,
        Enable,
        Disable,
    }

    private static int Deliver(IntPtr arg, Verb verb)
    {
        var logger = s_loggerFactory?.CreateLogger("星光");
        var action = ActionName(verb);

        // 整段包在 try 里：这是托管与父级之间唯一的一道缝，异常越过它等于把服务端一起拖下水
        // （ADR-0009）。一次交付失败只是这个插件没动成，父级照常跑。
        try
        {
            // UTF-8 而不是 UTF-16：原生侧的名字与路径本来就是 UTF-8，跨平台也是同一个字节形状。
            var text = arg == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(arg);
            if (string.IsNullOrEmpty(text))
            {
                logger?.LogError("父级交来的参数是空的");
                return 1;
            }

            if (s_host is null)
            {
                logger?.LogError("核心还没起来，{Action} {Name} 落空", action, text);
                return 1;
            }

            // 前三个的失败原因由核心自己报过（它会走 _report）。
            switch (verb)
            {
                case Verb.Load:
                    return s_host.Load(text) ? 0 : 1;
                case Verb.Enable:
                    return s_host.Enable(text) ? 0 : 1;
                case Verb.Disable:
                    return s_host.Disable(text) ? 0 : 1;
                default:
                    var result = s_host.Unload(text);
                    if (!result.Released)
                    {
                        logger?.LogError("卸载 {Name} 没完成：{Reason}", text, result.Error);
                    }

                    return result.Released ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "{Action} 时抛出，本次交付作废", action);
            return 1;
        }
    }

    private static string ActionName(Verb verb) => verb switch
    {
        Verb.Load => "装载",
        Verb.Unload => "卸载",
        Verb.Enable => "启用",
        _ => "停用",
    };

    private static int StartCore(bool scanPlugins)
    {
        s_loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));
        var logger = s_loggerFactory.CreateLogger("星光");

        try
        {
            // 服务端根就是进程的工作目录；配置与数据都相对它找（ADR-0018）。
            var root = Directory.GetCurrentDirectory();
            s_host = new PluginManagerHost(
                Path.Combine(root, "plugins"),
                Path.Combine(root, "data", "hosihikari"),
                Path.Combine(root, "config", "hosihikari"),
                entry => logger.LogError("加载问题：{Entry}", entry));
            s_host.AddProvider(new AssemblyPluginProvider());

            if (scanPlugins)
            {
                s_host.LoadAll();
                logger.LogInformation(
                    "已加载 {Count} 个插件：[{Names}]",
                    s_host.LoadedNames.Count,
                    string.Join(", ", s_host.LoadedNames));
            }
            else
            {
                logger.LogInformation("已就绪，等待父级交付插件");
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动失败");
            return 1;
        }
    }
}
