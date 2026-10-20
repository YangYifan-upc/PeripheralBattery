using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinForms = System.Windows.Forms;

namespace PeripheralBattery;

internal static class Theme
{
    public static readonly Brush Primary = New("#1a5fb4");
    public static readonly Brush PrimaryLight = New("#e8f0fb");
    public static readonly Brush Bg = New("#f3f5f9");
    public static readonly Brush Card = Brushes.White;
    public static readonly Brush Text = New("#1f2937");
    public static readonly Brush Text2 = New("#66707f");
    public static readonly Brush Border = New("#e3e7ee");
    public static readonly Brush Green = New("#2ecc71");
    public static readonly Brush Amber = New("#f5a623");
    public static readonly Brush Red = New("#e74c3c");
    public static readonly Brush Grey = New("#9e9e9e");

    public static Brush New(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static Brush ForPercent(int? percent) => percent switch
    {
        null => Grey,
        >= 50 => Green,
        >= 20 => Amber,
        _ => Red
    };

    public static FontFamily Font => new("Microsoft YaHei UI, Segoe UI");
}

internal sealed class FlyoutWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _footer;
    private readonly TextBlock _title;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;

    public FlyoutWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Font;
        ResizeMode = ResizeMode.NoResize;
        Deactivated += OnDeactivated;

        var root = new Border
        {
            Background = Theme.Card,
            BorderBrush = new SolidColorBrush(Color.FromArgb(26, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.16,
                Color = Colors.Black
            }
        };

        var outer = new StackPanel { Margin = new Thickness(0) };

        var header = new Grid { Margin = new Thickness(16, 14, 12, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            Text = "外设电量",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_title, 0);
        header.Children.Add(_title);

        var refresh = MakeIconButton("⟳", "立即刷新");
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);

        var settings = MakeIconButton("⚙", "设置");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        Grid.SetColumn(settings, 2);
        header.Children.Add(settings);

        outer.Children.Add(header);
        outer.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(12, 0, 12, 0) });

        _list.Margin = new Thickness(12, 6, 12, 6);
        outer.Children.Add(_list);

        _footer = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.Text2,
            Margin = new Thickness(16, 2, 16, 12),
            TextWrapping = TextWrapping.Wrap
        };
        outer.Children.Add(_footer);

        root.Child = outer;
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private static Button MakeIconButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 26,
            Height = 26,
            FontSize = 14,
            Foreground = Theme.Text2,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Margin = new Thickness(2, 0, 0, 0)
        };
        return b;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 延迟复核，避免点击内部控件时闪一下就消失
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsActive && IsVisible) Hide();
        };
        timer.Start();
    }

    public void Render(IReadOnlyList<DeviceView> devices, AppSettings settings)
    {
        _list.Children.Clear();

        if (devices.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "没有检测到可读取电量的外设。\n接收器插好后点右上角 ⟳ 刷新。",
                FontSize = 12,
                Foreground = Theme.Text2,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 12, 4, 12)
            });
        }

        foreach (var device in devices)
        {
            _list.Children.Add(BuildRow(device, settings));
        }

        _title.Text = devices.Count > 0 ? $"外设电量（{devices.Count}）" : "外设电量";
        _footer.Text = "数据来自设备主动上报，约每 3 秒刷新一次 · 只读，不修改设备";
    }

    private static UIElement BuildRow(DeviceView device, AppSettings settings)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 10) };

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 0);
        line.Children.Add(name);

        var percent = new TextBlock
        {
            Text = device.PercentText,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = device.Stale ? Theme.Grey : Theme.ForPercent(device.Percent)
        };
        Grid.SetColumn(percent, 1);
        line.Children.Add(percent);

        panel.Children.Add(line);
        panel.Children.Add(BuildBar(device));

        string detail = device.Percent.HasValue
            ? $"{device.ChargeText} · DPI {device.DpiX}×{device.DpiY} · 信号 {device.Rssi}"
            : "该设备未提供电量数据";
        if (device.Stale) detail = $"设备暂无上报（可能休眠或已关闭）· 最后读数 {device.LastSeen:HH:mm}";

        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Foreground = Theme.Text2,
            Margin = new Thickness(0, 5, 0, 0)
        });

        if (device.Percent is { } p && p <= settings.LowThreshold)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "电量偏低，建议尽快充电",
                FontSize = 11,
                Foreground = Theme.Red,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        return panel;
    }

    private static UIElement BuildBar(DeviceView device)
    {
        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Theme.PrimaryLight,
            Margin = new Thickness(0, 7, 0, 0)
        };

        var grid = new Grid();
        grid.Children.Add(track);
        if (device.Percent is { } p && p > 0)
        {
            var fill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = device.Stale ? Theme.Grey : Theme.ForPercent(device.Percent),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(6, 3.3 * p),
                Margin = new Thickness(0, 7, 0, 0)
            };
            grid.Children.Add(fill);
        }
        return grid;
    }

    public void ShowNearCursor()
    {
        var pos = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(pos);
        var work = screen.WorkingArea;

        UpdateLayout();
        double w = ActualWidth > 1 ? ActualWidth : Width;
        double h = ActualHeight > 1 ? ActualHeight : 200;

        double dpiX = 1.0, dpiY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
        }

        double workLeft = work.Left / dpiX, workTop = work.Top / dpiY;
        double workRight = work.Right / dpiX, workBottom = work.Bottom / dpiY;
        double cursorX = pos.X / dpiX, cursorY = pos.Y / dpiY;

        double left = cursorX - w / 2;
        left = Math.Max(workLeft + 8, Math.Min(left, workRight - w - 8));

        double top = cursorY - h - 12;
        if (top < workTop + 8) top = cursorY + 20;
        top = Math.Min(top, workBottom - h - 8);

        Left = left;
        Top = top;

        Show();
        Activate();
    }

    public void ShowCentered()
    {
        var work = SystemParameters.WorkArea;
        UpdateLayout();
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - ActualHeight) / 2;
        Show();
        Activate();
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly CheckBox _lowNotify;
    private readonly TextBox _threshold;
    private readonly TextBox _stale;
    private readonly CheckBox _autoStart;

    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        Title = "设置 · PeripheralBattery V0.1.0";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Theme.Bg;
        FontFamily = Theme.Font;
        ShowInTaskbar = true;

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "显示与提醒",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _lowNotify = new CheckBox
        {
            Content = "电量低于阈值时弹一次气泡提醒",
            IsChecked = current.NotifyOnLowBattery,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_lowNotify);

        panel.Children.Add(Label("低电量阈值（%）"));
        _threshold = new TextBox { Text = current.LowThreshold.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(_threshold);

        panel.Children.Add(Label("多久没收到上报就判为休眠（秒，建议 60–120）"));
        _stale = new TextBox { Text = current.StaleSeconds.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(_stale);

        _autoStart = new CheckBox
        {
            Content = "开机自动启动（只驻留托盘）",
            IsChecked = AutoStart.IsEnabled(),
            Margin = new Thickness(0, 16, 0, 0)
        };
        panel.Children.Add(_autoStart);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var ok = new Button { Content = "保存", Width = 84, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Save(current);
        var cancel = new Button { Content = "取消", Width = 84, Height = 30 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        panel.Children.Add(new TextBlock
        {
            Text = "PeripheralBattery V0.1.0 · 只读监视，不向设备发送任何命令",
            FontSize = 11,
            Foreground = Theme.Text2,
            Margin = new Thickness(0, 16, 0, 0)
        });

        Content = new Border { Background = Theme.Card, Child = panel, CornerRadius = new CornerRadius(10) };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Theme.Text2,
        Margin = new Thickness(0, 8, 0, 4)
    };

    private void Save(AppSettings current)
    {
        int threshold = int.TryParse(_threshold.Text.Trim(), out int t) ? t : current.LowThreshold;
        int stale = int.TryParse(_stale.Text.Trim(), out int s) ? s : current.StaleSeconds;

        current.LowThreshold = Math.Clamp(threshold, 5, 50);
        current.NotifyOnLowBattery = _lowNotify.IsChecked == true;
        current.StaleSeconds = Math.Clamp(stale, 5, 600);

        AutoStart.Set(_autoStart.IsChecked == true);
        current.Save();

        Result = current;
        Close();
    }
}
