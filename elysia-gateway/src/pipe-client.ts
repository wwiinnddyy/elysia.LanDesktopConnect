import { createConnection, type Socket } from 'node:net';

export interface IpcMessage {
  id: string;
  timestamp: number;
  type: string;
  sender: string;
  target?: string;
  payload?: unknown;
}

export class PluginPipeClient {
  private socket: Socket | null = null;
  private readonly messageHandlers: Array<(message: IpcMessage) => void> = [];
  private connected = false;
  private disconnecting = false;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private receiveBuffer = '';

  constructor(private readonly pipeName: string) {}

  async connect(): Promise<void> {
    if (this.connected) return;
    if (!this.pipeName) throw new Error('LMD_PLUGIN_PIPE is empty');

    this.disconnecting = false;
    return new Promise((resolve, reject) => {
      const socket = createConnection(this.pipeName);
      this.socket = socket;

      socket.once('connect', () => {
        this.connected = true;
        this.receiveBuffer = '';
        console.log('[PipeClient] Connected to plugin IPC server');
        resolve();
      });

      socket.on('data', (data) => this.handleData(data));
      socket.on('error', (error) => {
        console.error('[PipeClient] Error:', error.message);
        if (!this.connected) reject(error);
      });
      socket.on('close', () => {
        this.connected = false;
        this.socket = null;
        this.receiveBuffer = '';
        console.log('[PipeClient] Connection closed');
        if (!this.disconnecting) this.scheduleReconnect();
      });
    });
  }

  private handleData(data: string | Uint8Array): void {
    this.receiveBuffer += typeof data === 'string'
      ? data
      : new TextDecoder().decode(data);
    while (true) {
      const newlineIndex = this.receiveBuffer.indexOf('\n');
      if (newlineIndex < 0) return;

      const line = this.receiveBuffer.slice(0, newlineIndex).trim();
      this.receiveBuffer = this.receiveBuffer.slice(newlineIndex + 1);
      if (!line) continue;

      try {
        const message = JSON.parse(line) as IpcMessage;
        for (const handler of this.messageHandlers) {
          try {
            handler(message);
          } catch (error) {
            console.error('[PipeClient] Message handler failed:', error);
          }
        }
      } catch {
        console.error('[PipeClient] Failed to parse IPC frame');
      }
    }
  }

  async send(message: IpcMessage): Promise<void> {
    if (!this.socket || !this.connected) {
      throw new Error('Not connected to plugin IPC server');
    }

    await new Promise<void>((resolve, reject) => {
      this.socket!.write(`${JSON.stringify(message)}\n`, (error) => {
        if (error) reject(error);
        else resolve();
      });
    });
  }

  onMessage(handler: (message: IpcMessage) => void): void {
    this.messageHandlers.push(handler);
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer || this.disconnecting) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      console.log('[PipeClient] Attempting to reconnect...');
      void this.connect().catch(() => undefined);
    }, 2000);
  }

  async disconnect(): Promise<void> {
    this.disconnecting = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    const socket = this.socket;
    this.socket = null;
    this.connected = false;
    if (socket) {
      await new Promise<void>((resolve) => {
        socket.once('close', resolve);
        socket.end();
        setTimeout(() => {
          socket.destroy();
          resolve();
        }, 1000);
      });
    }
  }

  getIsConnected(): boolean {
    return this.connected;
  }
}
