using Avalonia.Markup.Xaml;
using LanMountainDesktop.AirAppSdk;

namespace Elysia.LanDesktopConnect.Settings;

public partial class IpcBridgeSettingsPage : AirAppSettingsPageBase
{
    public IpcBridgeSettingsPage()
    {
        InitializeComponent();
    }

    public IpcBridgeSettingsPage(IpcBridgeSettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (DataContext is IpcBridgeSettingsViewModel viewModel)
        {
            _ = viewModel.RefreshAsync();
        }
    }
}
