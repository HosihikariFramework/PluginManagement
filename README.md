# Hosihikari.PluginManager

[English](README.md) | [简体中文](README.zh_hans.md)

The **core** of the Hosihikari framework: it discovers plugin packages, loads them in dependency
order, puts each one in its own collectible ALC, drives its lifecycle, and — when any step fails —
takes down only that plugin.

The plugin contract lives in another repository and another package:
[`Hosihikari.Plugin.Abstractions`](https://www.nuget.org/packages/Hosihikari.Plugin.Abstractions).

## What is in this repository

| | |
|---|---|
| `src/` | The core: discovery, dependency graph, the assembly loader, lifecycle, failure isolation |
| `tools/Pack/` | The packaging tool: assembles plugin packages and rejects the rules whose failure would otherwise surface far from their cause |
| `test/Foo`, `test/Bar` | Two test plugins — and the packaging tool's first two users |
| `host/` | The assertion host: a plain .NET console, **touching no BDS, no Shim, no carrier** |

## Running it

```
dotnet run --project host/Hosihikari.PluginManager.Host.csproj -c Release
```

It runs the whole assertion list and exits non-zero if any of them fails. **`-c Release` is
required** — a Debug build keeps local variable references alive and makes the unload verification
fail spuriously.

That command is what CI runs, and it gets the contract from nuget.org the same way an external
plugin author does.

## How the contract is obtained

The projects under `src/` and `test/` pick between two references automatically, depending on
whether the sibling checkout exists:

- **Both repositories side by side** → `ProjectReference`. Changing the contract needs no repack.

The RID-first native library probe uses the McMaster fork; check that out as a sibling too (CI does).
- **This repository alone** (CI, and any external consumer) → `PackageReference`, pinned to an exact
  version.

## As a lip package

A `v*` tag builds `hosihikari-core-<version>.zip` and publishes it as
`github.com/HosihikariFramework/PluginManagement`. The package installs the managed core under
`plugins/hosihikari/`; the native Shim package declares a compatible Core version as a lip
dependency. The native and managed sides check `HostingContractVersion` at startup.

For a source deployment, `bash ./pack.sh <version>` builds the same archive locally.

## What is not here

- **The Shim** — the native layer that hosts the CLR inside the BDS process — is its own repository.
- **The out-of-process bridge** — RpcLoader is an official loader; it runs plugins in child processes
  instead of AOT-compiling source to IL. The core knows the `IPluginLoader` contract and nothing about
  the child process beyond it.
- **Any BDS-specific capability** — worlds, entities, commands and game events are outside the
  framework's capability surface.

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
