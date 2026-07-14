import { Elysia } from 'elysia';
import { PluginPipeClient, type IpcMessage } from './pipe-client';
import { MessageRouter } from './message-router';
import { ExternalAppManager } from './app-manager';
import { CapabilityRegistry } from './capability-registry';

const PIPE_NAME = process.env.LMD_PLUGIN_PIPE ?? '';
const DATA_DIR = process.env.LMD_PLUGIN_DATA_DIR ?? './data';
const PORT = Number.parseInt(process.env.LMD_GATEWAY_PORT ?? '0', 10);
const HOST = process.env.LMD_GATEWAY_HOST ?? '127.0.0.1';
const LOG_LEVEL = process.env.LMD_LOG_LEVEL ?? 'info';

const logger = {
  info: (message: string) => console.log(`[${new Date().toISOString()}] [INFO] ${message}`),
  error: (message: string) => console.error(`[${new Date().toISOString()}] [ERROR] ${message}`),
  debug: (message: string) => {
    if (LOG_LEVEL === 'debug') console.log(`[${new Date().toISOString()}] [DEBUG] ${message}`);
  },
};

async function main(): Promise<void> {
  if (!PIPE_NAME) throw new Error('LMD_PLUGIN_PIPE is required');

  logger.info('Starting Elysia IPC Gateway...');
  const pipeClient = new PluginPipeClient(PIPE_NAME);
  const appManager = new ExternalAppManager();
  const capabilityRegistry = new CapabilityRegistry();
  const messageRouter = new MessageRouter(pipeClient, appManager, capabilityRegistry);

  const app = new Elysia()
    .get('/health', () => ({
      status: 'ok',
      timestamp: Date.now(),
      connections: appManager.getConnectionCount(),
    }))
    .get('/api/capabilities', () => ({
      capabilities: capabilityRegistry.getHostCapabilities(),
    }))
    .get('/api/apps', () => ({
      apps: appManager.getAllApps(),
    }))
    .ws('/bridge', {
      open(ws) {
        logger.info(`New local WebSocket connection from ${ws.remoteAddress}`);
      },
      message(ws, rawMessage) {
        try {
          const message = (typeof rawMessage === 'string'
            ? JSON.parse(rawMessage)
            : rawMessage) as IpcMessage;
          logger.debug(`Received: ${JSON.stringify(message)}`);
          void messageRouter.handleExternalMessage(ws, message);
        } catch (error) {
          logger.error(`Failed to parse WebSocket message: ${String(error)}`);
          ws.send(JSON.stringify({
            id: crypto.randomUUID(),
            timestamp: Date.now(),
            type: 'error',
            sender: 'elysia-gateway',
            payload: { message: 'Invalid message format' },
          }));
        }
      },
      close(ws) {
        logger.info(`Local WebSocket connection closed: ${ws.remoteAddress}`);
        void messageRouter.handleExternalDisconnect(ws);
      },
    });

  app.listen({ port: PORT, hostname: HOST }, (server) => {
    logger.info(`Elysia IPC Gateway running on port ${server.port}`);
    logger.info(`Named pipe / Unix socket: ${PIPE_NAME}`);
    logger.info(`Data directory: ${DATA_DIR}`);
    void pipeClient.connect().catch((error: Error) => {
      logger.error(`Failed to connect to plugin IPC server: ${error.message}`);
    });
  });

  pipeClient.onMessage((message) => {
    void messageRouter.handleHostMessage(message);
  });

  const shutdown = async (): Promise<void> => {
    logger.info('Shutting down...');
    await pipeClient.disconnect();
    await app.stop();
    process.exit(0);
  };

  process.once('SIGINT', () => void shutdown());
  process.once('SIGTERM', () => void shutdown());
}

void main().catch((error) => {
  console.error('Failed to start gateway:', error);
  process.exit(1);
});
