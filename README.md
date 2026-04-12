# Elysia.LanDesktopConnect

基于 Elysia 的 IPC 网关插件，实现阑山桌面与外部应用的双向通信。

## 功能

- **Bun 运行时管理**：自动检测系统 Bun 安装，提供安装指引
- **Elysia 网关控制**：启动/停止/重启 Elysia IPC 网关
- **外部应用连接**：支持 Tauri、Web、Rust 等应用通过 WebSocket 连接
- **消息路由**：在阑山桌面与外部应用之间转发消息
- **设置页面**：完整的 Fluent Avalonia 设置界面

## 安装

1. 确保系统已安装 Bun：
   ```bash
   # Windows
   powershell -c "irm bun.sh/install.ps1 | iex"
   
   # Linux/macOS
   curl -fsSL https://bun.sh/install | bash
   ```

2. 安装插件到阑山桌面

3. 在设置页面启动 IPC 网关

## 架构

```
阑山桌面
    ↓ 加载插件
Elysia.LanDesktopConnect (C#)
    ↓ 启动 Bun 进程
Elysia 网关 (Bun/TypeScript)
    ↓ WebSocket
外部应用 (Tauri/Web/Rust)
```

## 开发

```bash
# 构建插件
dotnet build -c Release

# 生成的 .laapp 文件位于项目根目录
```

## 版本

当前版本：0.0.1
