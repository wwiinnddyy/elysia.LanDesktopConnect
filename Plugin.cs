using Elysia.LanDesktopConnect.Services;
using Elysia.LanDesktopConnect.Settings;
using Elysia.LanDesktopConnect.Widgets;
using LanMountainDesktop.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elysia.LanDesktopConnect;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => PluginLocalizer.Create(
            provider.GetRequiredService<IPluginRuntimeContext>()));
        services.AddSingleton<ElysiaSettingsService>();
        services.AddSingleton<BunDetector>();
        services.AddSingleton<GatewayIpcServer>();
        services.AddSingleton<IpcMessageRouter>();
        services.AddSingleton<BunProcessManager>();
        services.AddSingleton<ElysiaGatewayController>();
        services.AddSingleton<IHostedService, ElysiaGatewayHostedService>();

        services.AddTransient<IpcBridgeSettingsViewModel>();
        services.AddPluginSettingsSection<IpcBridgeSettingsPage>(
            id: ElysiaSettingsService.SectionId,
            titleLocalizationKey: "settings.ipc_bridge.title",
            descriptionLocalizationKey: "settings.ipc_bridge.description",
            iconKey: "PlugConnected",
            sortOrder: 100);

        services.AddPluginDesktopComponent<GatewayStatusWidget>(new PluginDesktopComponentOptions
        {
            ComponentId = GatewayStatusWidget.ComponentId,
            DisplayName = "Elysia IPC 网关状态",
            DisplayNameLocalizationKey = "widget.gateway_status.display_name",
            IconKey = "PlugConnected",
            Category = "Elysia",
            MinWidthCells = 3,
            MinHeightCells = 2,
            AllowDesktopPlacement = true,
            AllowStatusBarPlacement = false,
            ResizeMode = PluginDesktopComponentResizeMode.Free,
            CornerRadiusPreset = PluginCornerRadiusPreset.Component
        });

        services.AddPluginPublicIpc<IElysiaBridgePublicApi, ElysiaBridgePublicApi>(
            objectId: "elysia-bridge");
    }
}
