# Hosihikari.Testing

Test host and in-memory host services for Hosihikari plugins. It does not start BDS, create an
assembly load context, or read a manifest; it drives the public `IPlugin` contract directly.

```csharp
using Hosihikari.Testing;

using var host = new PluginTestHost(new MyPlugin());
host.RunLifecycle();

// host.HookLog: load → enable → disable → unload
```

The context is a `TestPluginContext`:

- `TestEvents` — synchronous event bus; handler exceptions land in `HandlerErrors`.
- `TestScheduler` — manual scheduler; `RunOnce()` and `RunPeriodic()` decide when work runs.
- `TestServices` — in-memory plugin service registry, first registration wins.
- `Faults` — strings reported through `IPluginContext.ReportFault`.
- `Logger`, `Configuration`, `CarrierServices` are replaceable for a test.

For integration tests that need real discovery, dependency ordering, or unload verification, use a
real `PluginManager` with a temporary plugin root. The core is distributed through lip, so this
package keeps its dependency to `Hosihikari.Plugin.Abstractions` only.

The package is MIT.
