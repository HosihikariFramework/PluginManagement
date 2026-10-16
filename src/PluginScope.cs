namespace Hosihikari.PluginManagement;

/// <summary>
/// 交给单个插件的宿主服务作用域。插件忘了退订的订阅、忘了释放的定时器，由它在卸载时一并收回。
///
/// 不收回的后果是实测出来的：挂在一个<em>共享</em>总线上的 handler 会把插件的 ALC 钉死，
/// 卸载验证永远过不去 —— 而且订阅当时毫无异样，只有卸载时才暴露（ADR-0004）。
/// 框架知道是哪一次订阅属于哪个插件，就不该把这件事留给作者自觉。
/// </summary>
internal sealed class PluginScope(IEventBus events, IScheduler scheduler) : IEventBus, IScheduler
{
    private readonly List<IDisposable> _owned = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) => Track(events.Subscribe(handler));

    public void Publish<TEvent>(TEvent @event) => events.Publish(@event);

    public IDisposable RunLater(TimeSpan delay, Action work) => Track(scheduler.RunLater(delay, work));

    public IDisposable RunEvery(TimeSpan interval, Action work) => Track(scheduler.RunEvery(interval, work));

    internal void ReleaseAll()
    {
        foreach (var owned in _owned.ToArray())
        {
            owned.Dispose();
        }

        _owned.Clear();
    }

    private IDisposable Track(IDisposable owned)
    {
        _owned.Add(owned);
        return new Handle(owned, _owned);
    }

    /// <summary>插件自己释放时，顺手把它从本方名单里划掉，名单不会随运行时长增长。</summary>
    private sealed class Handle(IDisposable inner, List<IDisposable> owner) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            owner.Remove(this);
        }
    }
}
