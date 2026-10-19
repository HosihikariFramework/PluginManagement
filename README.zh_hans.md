# Hosihikari.PluginManager

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的**核心**：把插件包发现出来、按依赖图装载、各自关进一个可回收的 ALC、驱动它的生命周期，并在任何一环失败时只让它自己倒下。

插件契约在另一个仓与另一个包：[`Hosihikari.Abstractions`](https://www.nuget.org/packages/Hosihikari.Abstractions)。

## 这个仓里有什么

| | |
|---|---|
| `src/` | 核心：发现、依赖图、程序集型 Provider、生命周期、失败隔离 |
| `tools/Pack/` | 打包工具：装配插件包，并在打包期拦下几条报错点离病因很远的规则 |
| `test/Foo`、`test/Bar` | 两个测试插件 —— 它们也是打包工具的头两个用户 |
| `host/` | 断言宿主：普通 .NET 控制台，**不碰 BDS、Shim、任何载体** |

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginManager.Host.csproj -c Release
```

它把整份断言清单跑一遍，任一条不过就以非零退出。**必须 `-c Release`** —— Debug 保留局部变量引用，会让卸载验证假失败。

CI 跑的就是这一条命令，走的也是外部插件作者拿到契约的那条路（从 nuget.org 取包）。

## 契约怎么被拿到

`src/` 与 `test/` 里的项目对 `Hosihikari.Abstractions` 有两种拿法，按兄弟目录在不在自动择一：

- **两个仓并列摆放** → `ProjectReference`。改契约不用重新打包。
- **只检出本仓**（CI、以及任何外部消费者）→ `PackageReference`，版本精确钉住。跟着契约的 API 面走。

本机库的 RID 优先解析走 McMaster 的 fork；它也必须作为兄弟目录检出（CI 就是这么摆的）。

## 不在这里的

- **Shim** —— 把 CLR 托管进 BDS 进程的那层原生代码，是独立的仓。
- **脚本型插件** —— 每种虚拟机一个仓；核心只认 Provider 契约，不认识任何脚本引擎。
- **任何 BDS 专有能力** —— 世界、实体、命令、游戏事件都不在能力面内。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
