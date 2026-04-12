using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Elysia.LanDesktopConnect.Services;

public class ElysiaGatewayHostedService : IHostedService
{
    private readonly BunDetector _bunDetector;
    private readonly BunProcessManager _bunManager;

    public ElysiaGatewayHostedService(
        BunDetector bunDetector,
        BunProcessManager bunManager)
    {
        _bunDetector = bunDetector;
        _bunManager = bunManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 检测 Bun
        var detectionResult = await _bunDetector.DetectAsync();
        
        if (!detectionResult.IsFound)
        {
            // Bun 未安装，记录日志但不阻止插件加载
            Console.WriteLine("[ElysiaGateway] Bun not found. Gateway will not start automatically.");
            return;
        }

        // 初始化 Bun 进程管理器
        var initialized = await _bunManager.InitializeAsync(detectionResult);
        if (!initialized)
        {
            Console.WriteLine("[ElysiaGateway] Failed to initialize Bun process manager.");
            return;
        }

        // 自动启动网关（可选，根据设置）
        // 这里不自动启动，让用户在设置页面手动启动
        Console.WriteLine($"[ElysiaGateway] Bun detected at {detectionResult.Path}. Ready to start.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 停止 Bun 进程
        await _bunManager.StopAsync();
    }
}
