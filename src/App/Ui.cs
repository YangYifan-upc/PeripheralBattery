using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace PeripheralBattery;

/// <summary>托盘图标绘制：数字 + 颜色分级。集中缓存，统一释放，避免 GDI 句柄泄漏。</summary>
internal static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, Icon> Cache = new();
    private static readonly object Gate = new();

    private static readonly Color Green = Color.FromArgb(46, 204, 113);
    private static readonly Color Amber = Color.FromArgb(245, 166, 35);
    private static readonly Color Red = Color.FromArgb(231, 76, 60);
    private static readonly Color Grey = Color.FromArgb(158, 158, 158);

    public static int IconSize
    {
        get
        {
            int s = WinForms.SystemInformation.SmallIconSize.Width;
            return Math.Clamp(s, 16, 64);
        }
    }

    public static Icon Get(int? percent, bool stale)
    {
        string key = $"{percent?.ToString() ?? "null"}|{stale}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var icon = BuildMultiSize(percent, stale);
            Cache[key] = icon;
            if (Cache.Count > 24) TrimCache(key);
            return icon;
        }
    }

    private static void TrimCache(string keep)
    {
        foreach (var k in Cache.Keys.ToList())
        {
            if (k == keep) continue;
            Cache[k].Dispose();
            Cache.Remove(k);
        }
    }

    private static Icon Draw(int size, int? percent, bool stale)
    {
        // 兼容旧调用：只生成单尺寸
        using var bmp = DrawBitmap(size, percent, stale, null, antiAlias: size > 32);
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    /// <summary>
    /// 打包成多尺寸 .ico。单个 24px 的 HICON 被系统按别的尺寸绘制时会被拉伸变糊，
    /// 多尺寸里各取所需才是清晰的唯一办法。
    /// 注意：System.Drawing.Icon 不认 PNG 压缩帧，必须用传统 DIB 帧。
    /// </summary>
    private static Icon BuildMultiSize(int? percent, bool stale)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48 };
        var frames = new List<(int size, byte[] dib)>();
        foreach (int s in sizes)
        {
            using var bmp = DrawBitmap(s, percent, stale, null, antiAlias: s > 32);
            frames.Add((s, EncodeDibFrame(bmp)));
        }

        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type = icon
            w.Write((ushort)frames.Count);

            int offset = 6 + 16 * frames.Count;
            foreach (var (size, dib) in frames)
            {
                w.Write((byte)size);         // width
                w.Write((byte)size);         // height
                w.Write((byte)0);            // palette
                w.Write((byte)0);            // reserved
                w.Write((ushort)1);          // planes
                w.Write((ushort)32);         // bpp
                w.Write(dib.Length);
                w.Write(offset);
                offset += dib.Length;
            }

            foreach (var (_, dib) in frames) w.Write(dib);
            w.Flush();
        }

        output.Position = 0;
        return new Icon(output);
    }

    /// <summary>把位图编码成 ICO 里的 DIB 帧：INFO 头 + 32bpp 倒序像素 + 全零 AND 掩码。</summary>
    private static byte[] EncodeDibFrame(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = (w + 31) / 32 * 4;
        var mask = new byte[maskStride * h];       // 全 0：不透明由 alpha 决定

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true);

        // BITMAPINFOHEADER
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);          // 高度含 AND 掩码
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(w * h * 4);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)     // 倒序：DIB 自下而上
            {
                IntPtr src = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(src, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        bw.Write(mask);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>单独绘制成位图，供诊断预览使用。</summary>
    /// <summary>
    /// 先按 3 倍尺寸矢量绘制再缩回目标尺寸（超采样），比在 24px 上直接抗锯齿锐利得多。
    /// antiAlias=false 时改为直接按原尺寸关抗锯齿绘制，用于对比。
    /// </summary>
    public static Bitmap DrawBitmap(int size, int? percent, bool stale, bool? lightTaskbar = null, bool antiAlias = true)
    {
        bool light = lightTaskbar ?? IsLightTaskbar();

        if (!antiAlias) return DrawCore(size, percent, stale, light, false);

        const int factor = 3;
        using var big = DrawCore(size * factor, percent, stale, light, true);
        var final = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(final);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);   // 防止边缘被当成透明黑
        g.DrawImage(big, new Rectangle(0, 0, size, size), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel, attrs);
        return final;
    }

    private static Bitmap DrawCore(int size, int? percent, bool stale, bool light, bool antiAlias)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = antiAlias ? SmoothingMode.AntiAlias : SmoothingMode.None;
        g.PixelOffsetMode = antiAlias ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
        g.TextRenderingHint = antiAlias ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.SingleBitPerPixelGridFit;
        g.Clear(Color.Transparent);

        Color status = percent switch
        {
            null => Grey,
            >= 50 => Green,
            >= 20 => Amber,
            _ => Red
        };

        // 深色任务栏配浅色数字，浅色任务栏配深色数字
        Color textColor = light ? Color.FromArgb(32, 42, 56) : Color.FromArgb(244, 247, 251);
        if (percent == null) textColor = Color.FromArgb(light ? 150 : 170, light ? 160 : 175, light ? 170 : 190);

        // 环宽取整 + 圆心对齐像素中心，边缘落在整像素上，避免抗锯齿把细环糊成灰带
        float stroke = Math.Max(1.5f, MathF.Round(size * 0.115f));
        var rect = new RectangleF(stroke / 2f, stroke / 2f, size - stroke, size - stroke);

        // 底色圆环（未充电部分）
        Color trackColor = light
            ? Color.FromArgb(82, 96, 110, 130)
            : Color.FromArgb(96, 236, 242, 250);
        using (var trackPen = new Pen(trackColor, stroke))
            g.DrawEllipse(trackPen, rect);

        // 进度圆弧：12 点方向开始顺时针
        if (percent is > 0)
        {
            float sweep = 360f * Math.Clamp(percent.Value, 1, 100) / 100f;
            if (sweep >= 359.5f) sweep = 359.5f;
            Color arcColor = stale ? Color.FromArgb(150, status) : status;
            using var arcPen = new Pen(arcColor, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            g.DrawArc(arcPen, rect, -90f, sweep);
        }

        string text = percent.HasValue ? percent.Value.ToString() : "?";
        float fontSize = text.Length switch
        {
            1 => size * 0.55f,
            2 => size * 0.42f,
            _ => size * 0.31f
        };
        fontSize = MathF.Round(fontSize * 2f) / 2f;  // 对齐半像素，字边缘更实

        // 文字走矢量轮廓：按字形实际墨迹范围居中，避免 GDI+ 字符串量宽左右不对称导致偏左
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, fontSize, new PointF(0, 0), StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        if (ink.Width > 0 && ink.Height > 0)
        {
            using var shift = new Matrix();
            shift.Translate((size - ink.Width) / 2f - ink.X, (size - ink.Height) / 2f - ink.Y);
            path.Transform(shift);
            using var textBrush = new SolidBrush(textColor);
            g.FillPath(textBrush, path);
        }

        return bmp;
    }

    /// <summary>诊断：量出数字实际墨迹的中心，检查是否真的居中。</summary>
    public static string DescribeInk(Bitmap bmp, bool light)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A < 180) continue;
                bool isDigit = light ? (c.R < 120 && c.G < 120 && c.B < 130) : (c.R > 200 && c.G > 200);
                if (!isDigit) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return "未识别到数字像素";

        float cx = (minX + maxX + 1) / 2f;
        float cy = (minY + maxY + 1) / 2f;
        float ideal = bmp.Width / 2f;
        return $"{bmp.Width}px 数字墨迹 x{minX}..{maxX} y{minY}..{maxY}，中心({cx:F1},{cy:F1})，" +
               $"理想({ideal:F1},{ideal:F1})，偏差 dx={cx - ideal:+0.0;-0.0;0} dy={cy - ideal:+0.0;-0.0;0}";
    }

    /// <summary>系统任务栏是浅色还是深色（决定数字用黑还是白）。</summary>
    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return true;
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void DisposeAll()
    {
        lock (Gate)
        {
            foreach (var icon in Cache.Values) icon.Dispose();
            Cache.Clear();
        }
    }
}

/// <summary>托盘宿主：图标、悬停、左键面板、右键菜单。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notify;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private Icon? _currentIcon;

    public event Action? ShowPanelRequested;
    public event Action? IconClicked;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? AutoStartToggled;

    public TrayHost()
    {
        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9f) };

        var showItem = new WinForms.ToolStripMenuItem("显示面板");
        showItem.Click += (_, _) => ShowPanelRequested?.Invoke();

        var refreshItem = new WinForms.ToolStripMenuItem("立即刷新");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke();

        _autoStartItem = new WinForms.ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _autoStartItem.Click += (_, _) => AutoStartToggled?.Invoke();

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var aboutItem = new WinForms.ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => WinForms.MessageBox.Show(
            "PeripheralBattery V0.1.0\n\n外设电量监视器（只读）\n" +
            "数据来源：设备主动上报的状态帧\n" +
            "不发送任何控制命令，不修改设备设置",
            "关于", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(showItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notify = new WinForms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = "外设电量监视器"
        };

        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) IconClicked?.Invoke();
        };

        SetIcon(IconRenderer.Get(null, false));
        Log.Info($"托盘图标尺寸 {IconRenderer.IconSize}px，任务栏浅色={IconRenderer.IsLightTaskbar()}");
    }

    public void SetAutoStartChecked(bool value) => _autoStartItem.Checked = value;

    public void SetIcon(Icon icon)
    {
        _currentIcon = icon;
        _notify.Icon = icon;
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 63 字符，超出会抛异常
        if (text.Length > 63) text = text[..62] + "…";
        try { _notify.Text = text; } catch { }
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notify.BalloonTipTitle = title;
            _notify.BalloonTipText = text;
            _notify.BalloonTipIcon = WinForms.ToolTipIcon.Warning;
            _notify.ShowBalloonTip(5000);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            _notify.Visible = false;
            _notify.Dispose();
            _menu.Dispose();
            _currentIcon?.Dispose();
        }
        catch { }
    }
}
