using Avalonia.Markup.Xaml;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Settings;

[SettingsPageInfo(
    id: "elysia.ipc-bridge",
    name: "Elysia IPC Bridge",
    category: SettingsPageCategory.Plugin,
    TitleLocalizationKey = "settings.ipc_bridge.title",
    DescriptionLocalizationKey = "settings.ipc_bridge.description",
    IconKey = "PlugConnected",
    SortOrder = 100)]
public partial class IpcBridgeSettingsPage : SettingsPageBase
{
    public IpcBridgeSettingsPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        // 页面显示时刷新数据
        if (DataContext is IpcBridgeSettingsViewModel vm)
        {
            vm.RefreshCommand?.Execute(null);
        }
    }
}
