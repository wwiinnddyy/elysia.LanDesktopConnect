import type { ServerWebSocket } from 'bun';

export interface ExternalApp {
  id: string;
  name: string;
  type: string;
  capabilities: string[];
  ws: ServerWebSocket<any>;
  connectedAt: number;
}

export class ExternalAppManager {
  private apps = new Map<string, ExternalApp>();
  private wsToApp = new Map<ServerWebSocket<any>, string>();

  registerApp(app: ExternalApp): void {
    this.apps.set(app.id, app);
    this.wsToApp.set(app.ws, app.id);
    console.log(`[AppManager] App registered: ${app.name} (${app.id})`);
  }

  removeApp(ws: ServerWebSocket<any>): void {
    const appId = this.wsToApp.get(ws);
    if (appId) {
      const app = this.apps.get(appId);
      if (app) {
        console.log(`[AppManager] App disconnected: ${app.name} (${appId})`);
        this.apps.delete(appId);
      }
      this.wsToApp.delete(ws);
    }
  }

  getApp(id: string): ExternalApp | undefined {
    return this.apps.get(id);
  }

  getAppByWs(ws: ServerWebSocket<any>): ExternalApp | undefined {
    const appId = this.wsToApp.get(ws);
    if (appId) {
      return this.apps.get(appId);
    }
    return undefined;
  }

  getAllApps(): Array<{
    id: string;
    name: string;
    type: string;
    capabilities: string[];
    connectedAt: number;
  }> {
    return Array.from(this.apps.values()).map(app => ({
      id: app.id,
      name: app.name,
      type: app.type,
      capabilities: app.capabilities,
      connectedAt: app.connectedAt,
    }));
  }

  getConnectionCount(): number {
    return this.apps.size;
  }

  sendToApp(appId: string, message: any): boolean {
    const app = this.apps.get(appId);
    if (app) {
      app.ws.send(JSON.stringify(message));
      return true;
    }
    return false;
  }

  broadcastToApps(message: any, excludeAppId?: string): void {
    for (const [id, app] of this.apps) {
      if (excludeAppId && id === excludeAppId) continue;
      app.ws.send(JSON.stringify(message));
    }
  }

  findAppsByCapability(capability: string): ExternalApp[] {
    return Array.from(this.apps.values()).filter(app =>
      app.capabilities.includes(capability)
    );
  }
}
