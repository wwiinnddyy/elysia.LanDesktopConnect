import { Elysia } from 'elysia';
import { websocket } from '@elysiajs/websocket';
import { PluginPipeClient } from './pipe-client';
import { MessageRouter } from './message-router';
import { ExternalAppManager } from './app-manager';
import { CapabilityRegistry } from './capability-registry';

// 从环境变量获取配置
const PIPE_NAME = process.env.LMD_PLUGIN_PIPE || '';
const DATA_DIR = process.env.LMD_PLUGIN_DATA_DIR || './data';
const PORT = parseInt(process.env.LMD_GATEWAY_PORT || '0');
const LOG_LEVEL = process.env.LMD_LOG_LEVEL || 'info';

// 日志工具
const logger = {
  info: (msg: string) => console.log(`[${new Date().toISOString()}] [INFO] ${msg}`),
  error: (msg: string) => console.error(`[${new Date().toISOString()}] [ERROR] ${msg}`),
  debug: (msg: string) => LOG_LEVEL === 'debug' && console.log(`[${new Date().toISOString()}] [DEBUG] ${msg}`),
};

async function main() {
  logger.info('Starting Elysia IPC Gateway...');

  // 初始化组件
  const pipeClient = new PluginPipeClient(PIPE_NAME);
  const appManager = new ExternalAppManager();
  const capabilityRegistry = new CapabilityRegistry();
  const messageRouter = new MessageRouter(pipeClient, appManager, capabilityRegistry);

  // 创建 Elysia 应用
  const app = new Elysia()
    .use(websocket())
    
    // 健康检查
    .get('/health', () => ({
      status: 'ok',
      timestamp: Date.now(),
      connections: appManager.getConnectionCount(),
    }))
    
    // 获取已注册的能力列表（阑山桌面暴露的功能）
    .get('/api/capabilities', () => ({
      capabilities: capabilityRegistry.getHostCapabilities(),
    }))
    
    // 获取已连接的外部应用
    .get('/api/apps', () => ({
      apps: appManager.getAllApps(),
    }))
    
    // WebSocket 端点 - 外部应用连接
    .ws('/bridge', {
      // 连接建立
      open(ws) {
        logger.info(`New connection from ${ws.remoteAddress}`);
      },
      
      // 接收消息
      message(ws, rawMessage) {
        try {
          const message = JSON.parse(rawMessage as string);
          logger.debug(`Received: ${JSON.stringify(message)}`);
          
          // 路由消息
          messageRouter.handleExternalMessage(ws, message);
        } catch (err) {
          logger.error(`Failed to parse message: ${err}`);
          ws.send(JSON.stringify({
            type: 'error',
            error: 'Invalid message format',
          }));
        }
      },
      
      // 连接关闭
      close(ws) {
        logger.info(`Connection closed: ${ws.remoteAddress}`);
        appManager.removeApp(ws);
      },
    });

  // 启动服务器
  const server = app.listen(PORT, (serverInfo) => {
    const actualPort = serverInfo.port;
    logger.info(`🚀 Elysia IPC Gateway running on port ${actualPort}`);
    logger.info(`📡 Named Pipe: ${PIPE_NAME}`);
    logger.info(`📁 Data Directory: ${DATA_DIR}`);
    
    // 连接到 C# 插件的 Named Pipe
    pipeClient.connect().then(() => {
      logger.info('✅ Connected to LanMountainDesktop plugin');
    }).catch((err) => {
      logger.error(`❌ Failed to connect to plugin: ${err.message}`);
    });
  });

  // 处理来自 C# 插件的消息
  pipeClient.onMessage((message) => {
    messageRouter.handleHostMessage(message);
  });

  // 优雅关闭
  process.on('SIGINT', async () => {
    logger.info('Shutting down...');
    await pipeClient.disconnect();
    server.stop();
    process.exit(0);
  });

  process.on('SIGTERM', async () => {
    logger.info('Shutting down...');
    await pipeClient.disconnect();
    server.stop();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error('Failed to start gateway:', err);
  process.exit(1);
});
