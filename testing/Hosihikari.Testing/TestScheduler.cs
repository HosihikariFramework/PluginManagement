using Hosihikari;

namespace Hosihikari.Testing;

/// <summary>手动调度器：测试决定何时执行一次性与周期性回调，不依赖真实时间。</summary>
public sealed class TestScheduler : IScheduler
{
    private readonly List<Action> _once = [];
    private readonly List<Action> _periodic = [];
    private readonly List<Exception> _errors = [];

    public IReadOnlyList<Exception> HandlerErrors => _errors;
    public int PendingOnce => _once.Count;
    public int PendingPeriodic => _periodic.Count;

    public IDisposable ScheduleOnce(TimeSpan delay, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _once.Add(work);
        return new Cancellation(() => _once.Remove(work));
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _periodic.Add(work);
        return new Cancellation(() => _periodic.Remove(work));
    }

    /// <summary>执行并清空所有一次性回调。</summary>
    public void RunOnce()
    {
        var pending = _once.ToArray();
        _once.Clear();
        Run(pending);
    }

    /// <summary>执行一轮所有周期性回调；回调保留，供下一轮继续。</summary>
    public void RunPeriodic() => Run(_periodic.ToArray());

    private void Run(IEnumerable<Action> work)
    {
        foreach (var action in work)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _errors.Add(ex);
            }
        }
    }

    private sealed class Cancellation(Action cancel) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancel();
        }
    }
}
