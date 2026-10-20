using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace PeripheralBattery;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase)))
            return ConsoleMode.Run(args);

        if (args.Any(a => a.Equals("--shot-icons", StringComparison.OrdinalIgnoreCase)))
            return ScreenshotMode.RunIcons(args);

        if (args.Any(a => a.Equals("--shot-ui", StringComparison.OrdinalIgnoreCase)))
            return ScreenshotMode.RunUi(args);

        if (args.Any(a => a.StartsWith("--shot", StringComparison.OrdinalIgnoreCase)))
            return ScreenshotMode.Run(args);

        var app = new BatteryApp();
        app.InitializeComponent(args);
        return app.Run();
    }

    internal static void TryAttachConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        catch { }
    }
}

internal sealed class BatteryApp : Application
{
    private AppSettings _settings = new();
    private DeviceMonitor? _monitor;
    private TrayHost? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private SingleInstance? _single;
    private string _lastLowNotified = "";

    public void InitializeComponent(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("界面线程异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未处理异常", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("后台任务异常", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Paths.Ensure();
        Log.Cleanup();
        Log.Info($"启动 V0.3.0，参数: {string.Join(' ', e.Args)}");

        _single = new SingleInstance();
        if (!_single.TryAcquire())
        {
            Log.Info("已有实例在运行，唤起后退出");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _monitor = new DeviceMonitor();
        _monitor.SetStaleSeconds(_settings.StaleSeconds);
        _monitor.Updated += OnDevicesUpdated;

        _tray = new TrayHost();
        _tray.SetAutoStartChecked(AutoStart.IsEnabled());
        _tray.ShowPanelRequested += () => ShowPanel(false);
        _tray.IconClicked += () => ShowPanel(true);
        _tray.RefreshRequested += ForceRefresh;
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += ExitApp;
        _tray.AutoStartToggled += ToggleAutoStart;

        _single.ShowRequested += () => Dispatcher.BeginInvoke(new Action(() => ShowPanel(false)));

        _flyout = new FlyoutWindow();
        _flyout.RefreshRequested += ForceRefresh;
        _flyout.SettingsRequested += OpenSettings;

        UpdateUi();

        bool silent = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!silent) ShowPanel(false);

        Log.Info("启动完成");
    }

    private void OnDevicesUpdated()
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateUi));

    private void UpdateUi()
    {
        if (_monitor == null || _tray == null) return;

        var devices = _monitor.Snapshot();
        var primary = _monitor.Primary();

        _tray.SetIcon(IconRenderer.Get(primary?.Percent, primary?.Stale ?? false));

        string tip = devices.Count == 0
            ? "外设电量：未检测到设备"
            : string.Join("  ", devices.Select(d => $"{d.Name} {d.PercentText}"));
        _tray.SetTooltip(tip);

        if (_flyout is { IsVisible: true }) _flyout.Render(devices, _settings);

        CheckLowBattery(primary);
    }

    private void CheckLowBattery(DeviceView? primary)
    {
        if (!_settings.NotifyOnLowBattery || primary?.Percent is null) return;
        if (primary.Percent.Value > _settings.LowThreshold) return;

        string key = $"{primary.Name}:{primary.Percent}";
        if (key == _lastLowNotified) return;
        _lastLowNotified = key;

        _tray?.ShowBalloon("电量偏低", $"{primary.Name} 剩余 {primary.Percent}%，建议尽快充电。");
        Log.Info($"低电量提醒: {primary.Name} {primary.Percent}%");
    }

    private void ShowPanel(bool toggle)
    {
        if (_monitor == null || _flyout == null) return;

        if (_flyout.IsVisible)
        {
            if (toggle) { _flyout.Hide(); return; }
            _flyout.Activate();
            return;
        }

        _flyout.Render(_monitor.Snapshot(), _settings);
        _flyout.ShowNearCursor();
    }

    private void ForceRefresh()
    {
        Log.Info("手动刷新");
        UpdateUi();
        if (_monitor != null)
        {
            // 重新枚举一次，接收器刚插上时能立刻发现
            foreach (var d in _monitor.Snapshot())
            {
                if ((DateTime.Now - d.LastSeen).TotalSeconds > 3) continue;
                return;
            }
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _flyout?.Hide();
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Closed += (_, _) =>
        {
            if (_settingsWindow?.Result != null)
            {
                _settings = _settingsWindow.Result;
                _monitor?.SetStaleSeconds(_settings.StaleSeconds);
                _tray?.SetAutoStartChecked(AutoStart.IsEnabled());
                UpdateUi();
            }
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ToggleAutoStart()
    {
        bool enabled = !AutoStart.IsEnabled();
        AutoStart.Set(enabled);
        _tray?.SetAutoStartChecked(AutoStart.IsEnabled());
        Log.Info($"开机自启 = {AutoStart.IsEnabled()}");
    }

    private void ExitApp()
    {
        Log.Info("用户请求退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("退出清理");
        try { _monitor?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { IconRenderer.DisposeAll(); } catch { }
        try { _single?.Dispose(); } catch { }
        base.OnExit(e);
    }
}

/// <summary>隐藏的诊断开关：渲染面板或截屏，便于核对界面。</summary>
internal static class ScreenshotMode
{
    public static int RunIcons(string[] args)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "icons.png");
        var arg = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase));
        if (arg != null) path = arg["--out=".Length..];
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);

        try
        {
            // --size=24 --percent=21 --scale=10：只画一个，放大看细节
            var sizeArg = args.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase));
            if (sizeArg != null)
            {
                int oneSize = int.TryParse(sizeArg["--size=".Length..], out int v) ? v : 24;
                var pctArg = args.FirstOrDefault(a => a.StartsWith("--percent=", StringComparison.OrdinalIgnoreCase));
                int onePct = pctArg != null && int.TryParse(pctArg["--percent=".Length..], out int p) ? p : 21;
                var scArg = args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase));
                int sc = scArg != null && int.TryParse(scArg["--scale=".Length..], out int q) ? Math.Clamp(q, 1, 12) : 8;

                using var one = IconRenderer.DrawBitmap(oneSize, onePct, false, true);
                Log.Info("浅色(超采样) " + IconRenderer.DescribeInk(one, true));
                using var oneCrisp = IconRenderer.DrawBitmap(oneSize, onePct, false, true, antiAlias: false);
                Log.Info("浅色(锐利) " + IconRenderer.DescribeInk(oneCrisp, true));

                using var bigLight = new System.Drawing.Bitmap(oneSize * sc * 2, oneSize * sc);
                using (var gg = System.Drawing.Graphics.FromImage(bigLight))
                {
                    gg.Clear(System.Drawing.Color.FromArgb(243, 243, 243));
                    gg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    gg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    gg.DrawImage(one, 0, 0, oneSize * sc, oneSize * sc);
                    gg.DrawImage(oneCrisp, oneSize * sc, 0, oneSize * sc, oneSize * sc);
                }
                bigLight.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Log.Info("已渲染单图标(左=抗锯齿 右=锐利): " + path);
                return 0;
            }

            int[] sizes = { 16, 20, 24, 32 };
            int[] percents = { 21, 55, 8, 100 };
            const int scale = 6;
            int cellW = sizes.Max() * scale + 14;
            int cellH = sizes.Max() * scale + 14;
            int rows = percents.Length;

            using var canvas = new System.Drawing.Bitmap(cellW * sizes.Length, cellH * rows * 2);
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                for (int theme = 0; theme < 2; theme++)
                {
                    bool light = theme == 0;
                    int bandTop = theme * cellH * rows;
                    using (var bg = new System.Drawing.SolidBrush(
                        light ? System.Drawing.Color.FromArgb(243, 243, 243) : System.Drawing.Color.FromArgb(32, 32, 32)))
                        g.FillRectangle(bg, 0, bandTop, canvas.Width, cellH * rows);

                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    for (int r = 0; r < rows; r++)
                    {
                        foreach (var (size, col) in sizes.Select((s, i) => (s, i)))
                        {
                            using var cell = IconRenderer.DrawBitmap(size, percents[r], false, light);
                            if (percents[r] is 21 or 100)
                                Log.Info((light ? "浅色 " : "深色 ") + IconRenderer.DescribeInk(cell, light));
                            int w = size * scale;
                            int x = col * cellW + (cellW - w) / 2;
                            int y = bandTop + r * cellH + (cellH - w) / 2;
                            g.DrawImage(cell, x, y, w, w);
                        }
                    }
                }
            }

            canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info("已渲染图标预览: " + path);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("渲染图标失败", ex);
            return 1;
        }
    }

    public static int RunUi(string[] args)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ui.png");
        var arg = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase));
        if (arg != null) path = arg["--out=".Length..];
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);

        try
        {
            using var monitor = new DeviceMonitor();
            Thread.Sleep(20000);   // 鼠标空闲时上报间隔可达数十秒，预览多等一会儿才能抓到

            var settings = AppSettings.Load();
            var devices = monitor.Snapshot();

            var flyout = new FlyoutWindow();
            flyout.Render(devices, settings);
            var content = (FrameworkElement)flyout.Content;

            content.Measure(new System.Windows.Size(360, double.PositiveInfinity));
            double h = Math.Max(80, content.DesiredSize.Height);
            content.Arrange(new Rect(0, 0, 360, h));
            content.UpdateLayout();

            const double scale = 2.0;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(360 * scale), (int)(h * scale), 96 * scale, 96 * scale,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(content);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);

            Log.Info("已渲染面板: " + path);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("渲染面板失败", ex);
            return 1;
        }
    }

    public static int Run(string[] args)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "screen.png");
        var arg = args.FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));
        if (arg != null) path = arg["--shot=".Length..];
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);

        try
        {
            var bounds = WinForms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size);

            // 可选：--crop=x,y,w,h --scale=3  放大局部，便于核对细节
            var cropArg = args.FirstOrDefault(a => a.StartsWith("--crop=", StringComparison.OrdinalIgnoreCase));
            var scaleArg = args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase));
            if (cropArg != null)
            {
                var parts = cropArg["--crop=".Length..].Split(',');
                if (parts.Length == 4 &&
                    int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy) &&
                    int.TryParse(parts[2], out int cw) && int.TryParse(parts[3], out int ch))
                {
                    int scale = 2;
                    if (scaleArg != null && int.TryParse(scaleArg["--scale=".Length..], out int sc)) scale = Math.Clamp(sc, 1, 4);

                    var rect = new System.Drawing.Rectangle(cx, cy, cw, ch);
                    rect.Intersect(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                    using var cropped = bmp.Clone(rect, bmp.PixelFormat);
                    using var scaled = new System.Drawing.Bitmap(cropped.Width * scale, cropped.Height * scale);
                    using (var g2 = System.Drawing.Graphics.FromImage(scaled))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g2.DrawImage(cropped, 0, 0, scaled.Width, scaled.Height);
                    }
                    scaled.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Log.Info($"已截图(局部) {rect} x{scale}: {path}");
                    return 0;
                }
            }

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info("已截图: " + path);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("截图失败", ex);
            return 1;
        }
    }
}

/// <summary>命令行模式：不开界面，把当前读数打到控制台和 console-out.txt，用于核对。</summary>
internal static class ConsoleMode
{
    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        int seconds = 25;
        var secArg = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
        if (secArg != null && int.TryParse(secArg["--seconds=".Length..], out int s) && s > 0 && s <= 120)
            seconds = s;

        var sb = new StringBuilder();
        void L(string text = "") => sb.AppendLine(text);

        L($"PeripheralBattery V0.3.0  只读读数核对   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"采样 {seconds} 秒…");

        using var monitor = new DeviceMonitor();

        var deadline = DateTime.Now.AddSeconds(seconds);
        // 等到「不再有新设备出现」再收工，避免只读到第一个设备就退出
        var lastChange = DateTime.Now;
        int lastCount = -1;
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(200);
            int count = monitor.Snapshot().Count;
            if (count != lastCount) { lastCount = count; lastChange = DateTime.Now; }
            if (count > 0 && (DateTime.Now - lastChange).TotalSeconds > 5) break;
        }

        var devices = monitor.Snapshot();
        L();
        if (devices.Count == 0)
        {
            L("未发现可读取电量的设备。");
            L("排查：1) 接收器是否插好 2) 设备是否开机 3) 是否被其它驱动独占");
        }
        else
        {
            foreach (var d in devices)
            {
                L($"设备: {d.Name}");
                L($"  电量      : {d.PercentText}");
                L($"  明细      : {d.DetailText}");
                L($"  最后上报  : {d.LastSeen:HH:mm:ss}{(d.Stale ? "（已超时）" : "")}");
                L($"  数据来源  : {d.SourcePath}");
                L();
            }
        }

        string text = sb.ToString();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "console-out.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch { }

        try { Console.WriteLine(text); } catch { }
        return 0;
    }
}
