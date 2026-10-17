namespace Hosihikari.PluginManagement;

/// <summary>
/// 交给单个插件的宿主服务作用域。插件忘了退订的订阅、忘了释放的定时器，由它在卸载时一并收回。
///
/// 不收回的后果是实测出来的：挂在一个<em>共享</em>总线上的 handler 会把插件的 ALC 钉死，
/// 卸载验证永远过不去 —— 而且订阅当时毫无异样，只有卸载时才暴露（ADR-0004）。
/// 框架知道是哪一次订阅属于哪个插件，就不该把这件事留给作者自觉。
/// </summary>
internal sealed class PluginScope(IEventBus events, IScheduler scheduler, PluginServiceRegistry registry)
    : IEventBus, IScheduler, IPluginServiceRegistry
{
    private readonly Lock _gate = new();
    private readonly HashSet<Handle> _owned = [];
    private readonly List<Type> _registered = [];
    private bool _released;

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) => Track(() => events.Subscribe(handler));

    public void Publish<TEvent>(TEvent @event) => events.Publish(@event);

    public IDisposable RunLater(TimeSpan delay, Action work) => Track(() => scheduler.RunLater(delay, work));

    public IDisposable RunEvery(TimeSpan interval, Action work) => Track(() => scheduler.RunEvery(interval, work));

    public bool Register<T>(T service) where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (!registry.Register(service))
            {
                return false;
            }

            _registered.Add(typeof(T));
            return true;
        }
    }

    public T? Get<T>() where T : class => registry.Get<T>();

    internal void ReleaseAll()
    {
        Handle[] owned;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owned = [.. _owned];
            _owned.Clear();
            foreach (var type in _registered)
            {
                registry.Remove(type);
            }

            _registered.Clear();
        }

        // Dispose outside the lock: a callback being cancelled may use this scope.
        foreach (var handle in owned)
        {
            handle.Dispose();
        }
    }

    private IDisposable Track(Func<IDisposable> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            var handle = new Handle(create(), this);
            _owned.Add(handle);
            return handle;
        }
    }

    private sealed class Handle(IDisposable inner, PluginScope owner) : IDisposable
    {
        private IDisposable? _inner = inner;

        public void Dispose()
        {
            var disposable = Interlocked.Exchange(ref _inner, null);
            if (disposable is null)
            {
                return;
            }

            lock (owner._gate)
            {
                owner._owned.Remove(this);
            }

            disposable.Dispose();
        }
    }
}
