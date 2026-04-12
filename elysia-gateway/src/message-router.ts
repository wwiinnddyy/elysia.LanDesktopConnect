import type { ServerWebSocket } from 'bun';
import type { PluginPipeClient, IpcMessage } from './pipe-client';
import type { ExternalAppManager } from './app-manager';
import type { CapabilityRegistry } from './capability-registry';

export class MessageRouter {
  constructor(
    private pipeClient: PluginPipeClient,
    private appManager: ExternalAppManager,
    private capabilityRegistry: CapabilityRegistry
  ) {}

  // 处理来自外部应用的消息
  async handleExternalMessage(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const { type, sender, target, payload } = message;

    switch (type) {
      case 'register':
        await this.handleAppRegister(ws, message);
        break;

      case 'unregister':
        await this.handleAppUnregister(ws, message);
        break;

      case 'capability:register':
        await this.handleCapabilityRegister(ws, message);
        break;

      case 'capability:call':
        await this.handleCapabilityCall(ws, message);
        break;

      case 'data:update':
        await this.handleDataUpdate(ws, message);
        break;

      case 'event:emit':
        await this.handleEventEmit(ws, message);
        break;

      case 'ping':
        ws.send(JSON.stringify({ type: 'pong', timestamp: Date.now() }));
        break;

      default:
        // 未知消息类型，转发到 C# 插件
        await this.forwardToHost(message);
    }
  }

  // 处理来自 C# 插件的消息
  async handleHostMessage(message: IpcMessage): Promise<void> {
    const { type, target, payload } = message;

    switch (type) {
      case 'host:capability:register':
        // 阑山桌面注册能力
        this.capabilityRegistry.registerHostCapability(payload);
        break;

      case 'host:capability:response':
        // 阑山桌面能力调用响应
        await this.forwardCapabilityResponse(message);
        break;

      case 'host:broadcast':
        // 阑山桌面广播消息到所有外部应用
        this.appManager.broadcastToApps(message);
        break;

      case 'host:send':
        // 阑山桌面发送消息到特定应用
        if (target) {
          this.appManager.sendToApp(target, message);
        }
        break;

      default:
        console.log(`[MessageRouter] Unknown host message type: ${type}`);
    }
  }

  // 应用注册
  private async handleAppRegister(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const { payload } = message;
    const app = {
      id: payload.id || `app-${Date.now()}`,
      name: payload.name || 'Unknown App',
      type: payload.type || 'unknown',
      capabilities: payload.capabilities || [],
      ws,
      connectedAt: Date.now(),
    };

    this.appManager.registerApp(app);

    // 通知 C# 插件
    await this.forwardToHost({
      ...message,
      sender: app.id,
    });

    // 发送注册成功响应
    ws.send(JSON.stringify({
      type: 'register:success',
      appId: app.id,
      timestamp: Date.now(),
    }));
  }

  // 应用注销
  private async handleAppUnregister(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (app) {
      // 移除该应用的所有能力
      this.capabilityRegistry.removeExternalCapabilities(app.id);
      
      // 通知 C# 插件
      await this.forwardToHost({
        ...message,
        sender: app.id,
      });
    }
  }

  // 注册能力
  private async handleCapabilityRegister(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    const { payload } = message;
    this.capabilityRegistry.registerExternalCapability({
      id: payload.id,
      name: payload.name,
      description: payload.description,
      parameters: payload.parameters,
      returns: payload.returns,
      appId: app.id,
    });

    // 通知 C# 插件有新能力可用
    await this.forwardToHost({
      type: 'capability:available',
      sender: app.id,
      payload: {
        appId: app.id,
        appName: app.name,
        capability: payload,
      },
    });
  }

  // 调用能力
  private async handleCapabilityCall(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const { target, payload } = message;
    
    if (!target) {
      ws.send(JSON.stringify({
        type: 'capability:error',
        error: 'Target not specified',
      }));
      return;
    }

    // 检查是否是调用阑山桌面的能力
    const hostCapability = this.capabilityRegistry.getHostCapability(payload.capabilityId);
    if (hostCapability) {
      // 转发到 C# 插件
      await this.forwardToHost({
        ...message,
        sender: this.appManager.getAppByWs(ws)?.id,
      });
      return;
    }

    // 检查是否是调用其他外部应用的能力
    const [targetAppId, capabilityId] = target.split(':');
    const externalCapability = this.capabilityRegistry.getExternalCapability(targetAppId, capabilityId);
    
    if (externalCapability) {
      // 转发到目标应用
      this.appManager.sendToApp(targetAppId, message);
      return;
    }

    // 能力未找到
    ws.send(JSON.stringify({
      type: 'capability:error',
      error: `Capability not found: ${payload.capabilityId}`,
    }));
  }

  // 数据更新
  private async handleDataUpdate(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    // 转发到 C# 插件
    await this.forwardToHost({
      ...message,
      sender: app.id,
    });

    // 广播给其他订阅的应用
    const { payload } = message;
    if (payload.broadcast) {
      this.appManager.broadcastToApps(message, app.id);
    }
  }

  // 事件发射
  private async handleEventEmit(ws: ServerWebSocket<any>, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    // 转发到 C# 插件
    await this.forwardToHost({
      ...message,
      sender: app.id,
    });

    // 广播给所有应用
    this.appManager.broadcastToApps(message, app.id);
  }

  // 转发能力调用响应
  private async forwardCapabilityResponse(message: IpcMessage): Promise<void> {
    const { target } = message;
    if (!target) return;

    // 转发到目标应用
    this.appManager.sendToApp(target, message);
  }

  // 转发消息到 C# 插件
  private async forwardToHost(message: IpcMessage): Promise<void> {
    if (!this.pipeClient.getIsConnected()) {
      console.error('[MessageRouter] Not connected to host plugin');
      return;
    }

    try {
      await this.pipeClient.send(message);
    } catch (err) {
      console.error('[MessageRouter] Failed to forward to host:', err);
    }
  }
}
