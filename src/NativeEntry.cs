using System.Runtime.InteropServices;

namespace Hosihikari.PluginManagement;

/// <summary>
/// 原生 Shim 调进来的那一个入口（ADR-0005）—— "原生 → 托管"这条缝的托管一侧。
/// 它由 <c>get_function_pointer</c> 按类型名与方法名取到，形状就是 <c>component_entry_point_fn</c>。
/// </summary>
public static class NativeEntry
{
    /// <summary>
    /// 启动核心。返回 0 表示成功，非 0 是错误码。
    ///
    /// **不抛异常**：在别人的进程里，托管异常越过这条边界等于把宿主一起拖下水（ADR-0009）。
    /// </summary>
    /// <param name="arg">扩展位，当前未用。</param>
    /// <param name="argSizeInBytes">上面那块的大小，当前未用。</param>
    [UnmanagedCallersOnly]
    public static int Start(IntPtr arg, int argSizeInBytes)
    {
        try
        {
            Console.WriteLine(
                $"[托管] 已接管：契约 api={PluginContract.ApiVersion}，运行时={Environment.Version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[托管] 启动失败：{ex}");
            return 1;
        }
    }
}
