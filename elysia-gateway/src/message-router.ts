import type { PluginPipeClient, IpcMessage } from './pipe-client';
import type { ExternalAppManager, GatewayWebSocket } from './app-manager';
import type { CapabilityRegistry } from './capability-registry';

type JsonRecord = Record<string, any>;

export class MessageRouter {
  constructor(
    private readonly pipeClient: PluginPipeClient,
    private readonly appManager: ExternalAppManager,
    private readonly capabilityRegistry: CapabilityRegistry,
  ) {}

  async handleExternalMessage(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    switch (message.type) {
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
        ws.send(JSON.stringify(this.createMessage('pong', 'elysia-gateway')));
        break;
      default:
        await this.forwardToHost(message);
        break;
    }
  }

  async handleHostMessage(message: IpcMessage): Promise<void> {
    switch (message.type) {
      case 'host:capability:register':
        this.capabilityRegistry.registerHostCapability(this.asRecord(message.payload) as any);
        break;
      case 'host:capability:response':
        this.forwardCapabilityResponse(message);
        break;
      case 'host:broadcast':
        this.appManager.broadcastToApps(this.createExternalEnvelope(message));
        break;
      case 'host:send':
        if (message.target) {
          this.appManager.sendToApp(message.target, this.createExternalEnvelope(message));
        }
        break;
      default:
        console.log(`[MessageRouter] Unknown host message type: ${message.type}`);
        break;
    }
  }

  async handleExternalDisconnect(ws: GatewayWebSocket): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    this.capabilityRegistry.removeExternalCapabilities(app.id);
    await this.forwardToHost(this.createMessage('app:disconnected', app.id, {
      id: app.id,
      name: app.name,
      type: app.type,
    }));
    this.appManager.removeApp(ws);
  }

  private async handleAppRegister(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const payload = this.asRecord(message.payload);
    const app = {
      id: this.asNonEmptyString(payload.id) ?? `app-${crypto.randomUUID()}`,
      name: this.asNonEmptyString(payload.name) ?? 'Unknown App',
      type: this.asNonEmptyString(payload.type) ?? 'unknown',
      capabilities: Array.isArray(payload.capabilities)
        ? payload.capabilities.filter((item): item is string => typeof item === 'string')
        : [],
      ws,
      connectedAt: Date.now(),
    };

    this.appManager.registerApp(app);
    await this.forwardToHost({ ...message, sender: app.id, payload });
    ws.send(JSON.stringify(this.createMessage('register:success', 'elysia-gateway', { appId: app.id })));
  }

  private async handleAppUnregister(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    this.capabilityRegistry.removeExternalCapabilities(app.id);
    await this.forwardToHost({ ...message, sender: app.id });
    this.appManager.removeApp(ws);
  }

  private async handleCapabilityRegister(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    const payload = this.asRecord(message.payload);
    const capabilityId = this.asNonEmptyString(payload.id);
    if (!capabilityId) {
      this.sendError(ws, 'capability:error', 'Capability id is required');
      return;
    }

    this.capabilityRegistry.registerExternalCapability({
      id: capabilityId,
      name: this.asNonEmptyString(payload.name) ?? capabilityId,
      description: this.asNonEmptyString(payload.description) ?? '',
      parameters: payload.parameters,
      returns: payload.returns,
      appId: app.id,
    });

    await this.forwardToHost(this.createMessage('capability:available', app.id, {
      appId: app.id,
      appName: app.name,
      capability: payload,
    }));
  }

  private async handleCapabilityCall(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const payload = this.asRecord(message.payload);
    const capabilityId = this.asNonEmptyString(payload.capabilityId);
    if (!message.target || !capabilityId) {
      this.sendError(ws, 'capability:error', 'Target and capabilityId are required');
      return;
    }

    if (this.capabilityRegistry.getHostCapability(capabilityId)) {
      const sender = this.appManager.getAppByWs(ws)?.id ?? message.sender;
      await this.forwardToHost({ ...message, sender });
      return;
    }

    const [targetAppId, targetCapabilityId] = message.target.split(':', 2);
    if (targetAppId && targetCapabilityId &&
        this.capabilityRegistry.getExternalCapability(targetAppId, targetCapabilityId)) {
      this.appManager.sendToApp(targetAppId, message);
      return;
    }

    this.sendError(ws, 'capability:error', `Capability not found: ${capabilityId}`);
  }

  private async handleDataUpdate(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    const forwarded = { ...message, sender: app.id };
    await this.forwardToHost(forwarded);
    if (this.asRecord(message.payload).broadcast === true) {
      this.appManager.broadcastToApps(forwarded, app.id);
    }
  }

  private async handleEventEmit(ws: GatewayWebSocket, message: IpcMessage): Promise<void> {
    const app = this.appManager.getAppByWs(ws);
    if (!app) return;

    const forwarded = { ...message, sender: app.id };
    await this.forwardToHost(forwarded);
    this.appManager.broadcastToApps(forwarded, app.id);
  }

  private forwardCapabilityResponse(message: IpcMessage): void {
    if (message.target) this.appManager.sendToApp(message.target, message);
  }

  private async forwardToHost(message: IpcMessage): Promise<void> {
    if (!this.pipeClient.getIsConnected()) {
      console.error('[MessageRouter] Not connected to host plugin');
      return;
    }

    try {
      await this.pipeClient.send(message);
    } catch (error) {
      console.error('[MessageRouter] Failed to forward to host:', error);
    }
  }

  private createExternalEnvelope(message: IpcMessage): IpcMessage {
    const payload = this.asRecord(message.payload);
    const type = this.asNonEmptyString(payload.type);
    if (!type) return message;

    return {
      id: message.id,
      timestamp: message.timestamp,
      type,
      sender: message.sender,
      target: message.target,
      payload: payload.payload,
    };
  }

  private sendError(ws: GatewayWebSocket, type: string, error: string): void {
    ws.send(JSON.stringify(this.createMessage(type, 'elysia-gateway', { error })));
  }

  private createMessage(type: string, sender: string, payload?: unknown): IpcMessage {
    return {
      id: crypto.randomUUID(),
      timestamp: Date.now(),
      type,
      sender,
      payload,
    };
  }

  private asRecord(value: unknown): JsonRecord {
    return value && typeof value === 'object' && !Array.isArray(value)
      ? value as JsonRecord
      : {};
  }

  private asNonEmptyString(value: unknown): string | undefined {
    return typeof value === 'string' && value.trim() ? value.trim() : undefined;
  }
}
