using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LanMountainDesktop.PluginSdk;
using Elysia.LanDesktopConnect.Services;

namespace Elysia.LanDesktopConnect.Widgets;

internal sealed class GatewayStatusWidget : Border
{
    public const string ComponentId = "Elysia.LanDesktopConnect.GatewayStatus";

    private readonly PluginDesktopComponentContext _context;
    private readonly BunProcessManager _bunManager;
    private readonly IpcMessageRouter _router;
    private readonly IPluginMessageBus _messageBus;
    private readonly PluginLocalizer _localizer;
    private readonly Border _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _endpointText;
    private readonly TextBlock _appsText;
    private readonly List<IDisposable> _subscriptions = [];

    public GatewayStatusWidget(PluginDesktopComponentContext context)
    {
        _context = context;
        _bunManager = context.GetService<BunProcessManager>()
            ?? throw new InvalidOperationException("BunProcessManager is not available.");
        _router = context.GetService<IpcMessageRouter>()
            ?? throw new InvalidOperationException("IpcMessageRouter is not available.");
        _messageBus = context.GetService<IPluginMessageBus>()
            ?? throw new InvalidOperationException("IPluginMessageBus is not available.");
        _localizer = PluginLocalizer.Create(context);

        _statusDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusText = new TextBlock
        {
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _endpointText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#D9EAF7FF")),
            TextWrapping = TextWrapping.Wrap
        };
        _appsText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#B8EAF7FF")),
            TextWrapping = TextWrapping.Wrap
        };

        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.Parse("#FF071A2D"), 0),
                new GradientStop(Color.Parse("#FF0B4A6F"), 0.58),
                new GradientStop(Color.Parse("#FF0EA5A8"), 1)
            ]
        };
        BorderBrush = new SolidColorBrush(Color.Parse("#667DD3FC"));
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                _statusDot,
                new TextBlock
                {
                    Text = _localizer.GetString("widget.gateway_status.title", "Elysia IPC"),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = "API 5",
                    Foreground = new SolidColorBrush(Color.Parse("#C7EAF7FF")),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(titleRow.Children[1], 1);
        Grid.SetColumn(titleRow.Children[2], 2);

        Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                titleRow,
                _statusText,
                _endpointText,
                _appsText
            }
        };

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        SizeChanged += (_, _) => ApplyScale();

        ApplyScale();
        Refresh();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_subscriptions.Count == 0)
        {
            _subscriptions.Add(_messageBus.Subscribe<BunStatusChangedEvent>(_ => PostRefresh()));
            _subscriptions.Add(_messageBus.Subscribe<ConnectedAppsChangedEvent>(_ => PostRefresh()));
            _subscriptions.Add(_messageBus.Subscribe<GatewayTransportConnectionChangedEvent>(_ => PostRefresh()));
        }

        Refresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private void PostRefresh()
    {
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var appCount = _router.GetConnectedApps().Count;
        var (color, status) = _bunManager.Status switch
        {
            BunStatus.Running when _bunManager.IsTransportConnected =>
                (Color.Parse("#FF5EEAD4"), _localizer.GetString("widget.gateway_status.connected", "网关运行中，IPC 已连接")),
            BunStatus.Running =>
                (Color.Parse("#FFFBBF24"), _localizer.GetString("widget.gateway_status.waiting", "网关运行中，等待 IPC 连接")),
            BunStatus.Starting =>
                (Color.Parse("#FF7DD3FC"), _localizer.GetString("gateway.status.starting", "正在启动...")),
            BunStatus.Error =>
                (Color.Parse("#FFF87171"), _localizer.GetString("gateway.status.error", "发生错误")),
            BunStatus.NotInstalled =>
                (Color.Parse("#FFF87171"), _localizer.GetString("bun.not_installed", "未安装 Bun")),
            _ =>
                (Color.Parse("#FF94A3B8"), _localizer.GetString("gateway.status.stopped", "已停止"))
        };

        _statusDot.Background = new SolidColorBrush(color);
        _statusText.Text = status;
        _endpointText.Text = _bunManager.GatewayPort is { } port
            ? _localizer.Format("widget.gateway_status.endpoint", "本机端点：ws://127.0.0.1:{0}/bridge", port)
            : _localizer.GetString("widget.gateway_status.no_endpoint", "尚未分配监听端口");
        _appsText.Text = _localizer.Format(
            "widget.gateway_status.apps",
            "已连接应用：{0}",
            appCount);
    }

    private void ApplyScale()
    {
        var width = Bounds.Width > 1 ? Bounds.Width : _context.CellSize * 3;
        var height = Bounds.Height > 1 ? Bounds.Height : _context.CellSize * 2;
        var basis = Math.Max(_context.CellSize * 2, Math.Min(width, height));

        Padding = new Thickness(Math.Clamp(basis * 0.09, 14, 24));
        CornerRadius = new CornerRadius(_context.ResolveCornerRadius(
            PluginCornerRadiusPreset.Component,
            minimum: 12,
            maximum: 28));
        _statusText.FontSize = Math.Clamp(basis * 0.085, 14, 20);
        _endpointText.FontSize = Math.Clamp(basis * 0.058, 11, 15);
        _appsText.FontSize = Math.Clamp(basis * 0.052, 10, 14);
    }
}
