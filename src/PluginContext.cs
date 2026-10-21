using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hosihikari.Internal;

/// <summary>插件在框架里的状态。</summary>
internal enum PluginState
{
    /// <summary>在场：实例已建、钩子已挂，事件订阅与定时器归它的作用域管。</summary>
    Loaded,

    /// <summary>已放下：实例与作用域都放开了，只等 ALC 被真正回收。回收没通过前它仍占着内存（ADR-0004）。</summary>
    Detached,
}

internal sealed class PluginContext(
    string name,
    string packageDirectory,
    string dataDirectory,
    ILogger logger,
    IConfiguration configuration,
    PluginScope scope,
    IServiceProvider carrierServices,
    PluginManager host,
    Guid instanceId,
    Action<string> reportFault) : IPluginContext
{
    internal static readonly IServiceProvider Empty = new EmptyServices();

    public string Name => name;

    public string PackageDirectory => packageDirectory;

    public string DataDirectory => dataDirectory;

    public ILogger Logger => logger;

    public IConfiguration Configuration => configuration;

    public IEventBus Events => scope;

    public IScheduler Scheduler => scope;

    public IPluginServiceRegistry PluginServices => scope;

    public IServiceProvider CarrierServices => carrierServices;

    /// <summary>Loader 在钩子之外发现插件死了时走这里（ADR-0030）。</summary>
    public void ReportFault(string reason) => host.ReportFault(name, instanceId, reason);

    /// <summary>Loader 是个插件包，装载时用这个把自己交出去（ADR-0002）。</summary>
    public bool RegisterLoader(IPluginLoader loader)
    {
        if (!host.TryAddLoader(loader, out var problem))
        {
            if (problem is not null)
            {
                reportFault(problem);
            }

            return false;
        }

        host.MarkLoader(instanceId, loader);
        return true;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
