using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginManagement;

/// <summary>
/// 把托管的日志转发给父级的日志器（ADR-0007 的出口一侧）。
///
/// 原生侧在启动时递进来一个 C 回调：<c>void (*)(void* user, int level, const char* utf8)</c>。
/// 等级由这里映射成整数，文本一律 UTF-8、以 null 结尾 —— 与这条缝上的其它字符串一致。
/// </summary>
internal sealed unsafe class NativeForwardingLoggerProvider(IntPtr callback, IntPtr user) : ILoggerProvider
{
    private static long s_generation;

    /// <summary>
    /// 旧原生日志器要被卸载时推进代次；旧 logger 捕获的代次不再相等，后续日志直接丢弃，
    /// 不会跳进已经解除映射的父级回调。
    /// </summary>
    internal static void AdvanceGeneration() => Interlocked.Increment(ref s_generation);

    private readonly long _generation = Volatile.Read(ref s_generation);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, callback, user, _generation);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, IntPtr callback, IntPtr user, long generation) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || generation != Volatile.Read(ref s_generation))
            {
                return;
            }

            // 异常整段带上：在别人的进程里没有调试器，消息就是唯一的线索（ADR-0009）。
            var message = $"{category}: {formatter(state, error)}";
            if (error is not null)
            {
                // 异步与迭代器的栈是编译器生成的（MoveNext、<Foo>d__3），不去谜读不懂。
                message += " —— " + error.Demystify();
            }

            var bytes = Encoding.UTF8.GetBytes(message + "\0");
            fixed (byte* text = bytes)
            {
                ((delegate* unmanaged[Cdecl]<IntPtr, int, byte*, void>)callback)(user, Level(logLevel), text);
            }
        }

        /// <summary>0 调试、1 信息、2 警告、3 错误、4 致命 —— 与两个载体的桥一致。</summary>
        private static int Level(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => 0,
            LogLevel.Information => 1,
            LogLevel.Warning => 2,
            LogLevel.Error => 3,
            _ => 4,
        };
    }
}
