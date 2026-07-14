# Elysia.LanDesktopConnect

Elysia IPC Bridge 是阑山桌面的本地通信插件。它在插件进程内托管安全边界清晰的 IPC 服务端，并启动一个仅监听 `127.0.0.1` 的 Elysia WebSocket 网关，让 Tauri、Web、Rust、Node.js 等本机应用能够与阑山桌面交换消息。

当前版本为 **0.1.0**，面向生产 `LanMountainDesktop.PluginSdk` / API **5.0.0**，最低宿主版本为 **0.8.6**。

## 功能

- 自动检测 Bun，支持手动启动、停止、重启和异常退出自动恢复。
- Windows 使用命名管道，Linux/macOS 使用 Unix Domain Socket 连接 C# 插件与 Bun 网关。
- 提供 `ws://127.0.0.1:<port>/bridge`、`/health`、`/api/apps` 和 `/api/capabilities`。
- 提供 API 5 自定义设置页 `IpcBridgeSettingsSection`。
- 提供桌面组件 `Elysia.LanDesktopConnect.GatewayStatus`。
- 通过对象 ID `elysia-bridge` 暴露强类型公共 IPC 服务 `IElysiaBridgePublicApi`。
- 使用 PluginSdk v5 设置服务保存配置，并在首次升级时导入旧版 `Data/settings.json`。
- 提供中英文本地化、market-manifest v2、可重复发布脚本和包一致性校验。

## 运行要求

- LanMountainDesktop 0.8.6 或更高版本。
- LanMountainDesktop Plugin API 5.0.0。
- .NET 10（由宿主提供）。
- [Bun](https://bun.sh/docs/installation)。建议使用当前稳定版。
- 首次启动网关时需要联网恢复 `elysia-gateway/bun.lock` 固定的生产依赖；之后使用本地 `node_modules` 缓存。

## 安装与使用

1. 从 GitHub Release 下载 `Elysia.LanDesktopConnect.0.1.0.laapp`。
2. 在阑山桌面插件市场或插件管理页安装该文件。
3. 打开“设置 → 插件 → Elysia IPC Bridge”。
4. 确认 Bun 检测成功，然后启动网关。
5. 从设置页或“Elysia IPC 网关状态”桌面组件查看端口和连接状态。

网关默认自动分配可用端口，也可以在停止状态下设置 `1024` 到 `65535` 的固定端口。

## WebSocket 消息

外部应用连接 `/bridge` 后应先注册：

```json
{
  "id": "request-1",
  "timestamp": 1783987200000,
  "type": "register",
  "sender": "my-app",
  "payload": {
    "id": "my-app",
    "name": "My Application",
    "type": "tauri",
    "capabilities": ["example.echo"]
  }
}
```

随后可以发送 `capability:register`、`capability:call`、`data:update`、`event:emit`、`ping` 或自定义消息。每条消息使用换行分隔的 JSON 在插件 IPC 传输中转发；WebSocket 客户端收发普通 JSON 文本帧。

## 安全说明

- 网关固定监听 `127.0.0.1`，不会主动暴露到局域网。
- 当前协议不提供应用级身份认证。不要通过反向代理、端口转发或防火墙规则将网关暴露到其他设备。
- 外部应用传入的数据应被视为不可信；消费 `ExternalMessageReceivedEvent` 时仍需校验消息类型和负载。
- 日志和插件设置保存在宿主为该插件分配的数据目录中。

## 架构

```text
LanMountainDesktop (PluginSdk API 5)
  ├─ 设置页 / 桌面状态组件 / 公共 IPC 服务
  ├─ BunProcessManager
  └─ GatewayIpcServer
       └─ Windows Named Pipe / Unix Domain Socket
            └─ Bun + Elysia localhost WebSocket gateway
                 └─ Tauri / Web / Rust / Node.js applications
```

## 开发与验证

仓库旁需要有 `LanMountainDesktop` 源码，用于生成未发布到 NuGet.org 的生产 PluginSdk 5 本地包：

```powershell
.\scripts\Initialize-LocalPackageFeed.ps1

Push-Location .\elysia-gateway
bun install --frozen-lockfile
bun run typecheck
Pop-Location

.\scripts\Test-GatewaySmoke.ps1

dotnet restore .\Elysia.LanDesktopConnect.csproj --force --no-cache
dotnet build .\Elysia.LanDesktopConnect.csproj -c Release --no-restore

.\scripts\New-MarketManifest.ps1 `
  -TemplatePath .\airappmarket-entry.template.json `
  -PackagePath .\Elysia.LanDesktopConnect.0.1.0.laapp `
  -Version 0.1.0 `
  -ReleaseTag v0.1.0 `
  -OutputPath .\market-manifest.json

.\scripts\Test-PluginConsistency.ps1 `
  -PackagePath .\Elysia.LanDesktopConnect.0.1.0.laapp `
  -MarketManifestPath .\market-manifest.json
```

发布前还应使用 LanAirApp 的 IndexBuilder 交叉校验实际安装包与市场清单：

```powershell
dotnet run --project ..\LanAirApp\airappmarket\tools\AirAppMarket.IndexBuilder -- `
  --validate-release-package .\Elysia.LanDesktopConnect.0.1.0.laapp `
  --market-manifest .\market-manifest.json `
  --plugin-id Elysia.LanDesktopConnect
```

## 发布约束

- `0.0.1` 是已经发布的 API 4 包，`0.0.2` 是旧源码版本；API 5 从 `0.1.0` 开始，发布脚本会拒绝复用旧版本号。
- Release 必须同时上传 `.laapp` 和 schema `2.0.0` 的 `market-manifest.json`。
- LanAirApp 官方 registry 在真实 Release 资产上线并通过交叉校验前应继续保持禁用；不要仅凭本地包提前重新启用。

## License

本项目使用 [GNU GPL v3](LICENSE)。
