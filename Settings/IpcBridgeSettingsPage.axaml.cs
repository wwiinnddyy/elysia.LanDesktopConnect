using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Settings;

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
