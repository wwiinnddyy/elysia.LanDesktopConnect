import { connect, Socket } from 'net';
import { createConnection } from 'net';

export interface IpcMessage {
  id: string;
  timestamp: number;
  type: string;
  sender: string;
  target?: string;
  payload?: any;
}

export class PluginPipeClient {
  private socket: Socket | null = null;
  private pipeName: string;
  private messageHandlers: ((message: IpcMessage) => void)[] = [];
  private isConnected = false;
  private reconnectTimer: Timer | null = null;

  constructor(pipeName: string) {
    this.pipeName = pipeName;
  }

  async connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      try {
        // Windows Named Pipe
        if (process.platform === 'win32') {
          this.socket = createConnection(this.pipeName);
        } else {
          // Unix Domain Socket
          this.socket = createConnection(this.pipeName);
        }

        this.socket.on('connect', () => {
          console.log('[PipeClient] Connected to plugin');
          this.isConnected = true;
          resolve();
        });

        this.socket.on('data', (data) => {
          this.handleData(data);
        });

        this.socket.on('error', (err) => {
          console.error('[PipeClient] Error:', err.message);
          if (!this.isConnected) {
            reject(err);
          }
        });

        this.socket.on('close', () => {
          console.log('[PipeClient] Connection closed');
          this.isConnected = false;
          this.scheduleReconnect();
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  private handleData(data: Buffer) {
    const lines = data.toString().split('\n');
    for (const line of lines) {
      if (!line.trim()) continue;
      
      try {
        const message: IpcMessage = JSON.parse(line);
        // 通知所有消息处理器
        this.messageHandlers.forEach(handler => handler(message));
      } catch (err) {
        console.error('[PipeClient] Failed to parse message:', line);
      }
    }
  }

  send(message: IpcMessage): Promise<void> {
    return new Promise((resolve, reject) => {
      if (!this.socket || !this.isConnected) {
        reject(new Error('Not connected to plugin'));
        return;
      }

      const data = JSON.stringify(message) + '\n';
      this.socket.write(data, (err) => {
        if (err) reject(err);
        else resolve();
      });
    });
  }

  onMessage(handler: (message: IpcMessage) => void) {
    this.messageHandlers.push(handler);
  }

  private scheduleReconnect() {
    if (this.reconnectTimer) return;
    
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      console.log('[PipeClient] Attempting to reconnect...');
      this.connect().catch(() => {
        // 重连失败，继续尝试
      });
    }, 5000);
  }

  async disconnect(): Promise<void> {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    
    if (this.socket) {
      this.socket.end();
      this.socket = null;
    }
    this.isConnected = false;
  }

  getIsConnected(): boolean {
    return this.isConnected;
  }
}
