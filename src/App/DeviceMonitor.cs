using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
namespace PeripheralBattery;

/// <summary>从设备主动上报的 19 字节状态帧里解出的字段。</summary>
internal sealed class StatusFrame
{
    public byte ReportId;
    public int Protocol;      // 0=2.4G
    public int Sensor;
    public int CpiLevel;
    public int DpiX, DpiY;
    public int BatteryStatus; // 实测：1=未充电。其它取值含义未验证，不做推断
    public int Percent;
    public int ConfigSlot;
    public int UsbPid;
    public int Rssi;
    public bool RfConnected;
    public DateTime Time = DateTime.Now;
}

/// <summary>
/// 状态帧解码。字段布局来自雷柏网页驱动（A-HUB）里 VTLM20RT 鼠标的 parseUserCustomData：
///   e[0]=协议|传感器, e[1]=CPI 档, e[2..3]=X DPI, e[4..5]=Y DPI,
///   e[6]=充电状态, e[7]=电量百分比, e[8]=灯效, e[9]=回报率, e[10..], e[11]=配置槽,
///   e[12..13]=usbPid, e[14]=按键类型, e[15]=射频类型, e[16]=传感器模式, e[17]=连接|RSSI
/// 入参含首字节 report id，因此整体下标 +1。
/// 已用真机核对：驱动显示 21% 时该字节为 0x15=21，DPI 3700 与驱动一致。
/// </summary>
internal static class StatusDecoder
{
    public static bool TryDecode(byte[] d, out StatusFrame f)
    {
        f = new StatusFrame();
        if (d.Length < 18) return false;
        if (d[0] == 0) return false;

        int e0 = d[1];
        int protocol = e0 & 15;
        int sensor = e0 >> 4 & 15;
        int dpiX = d[3] | d[4] << 8;
        int dpiY = d[5] | d[6] << 8;
        int batStatus = d[7];
        int percent = d[8];
        int cfg = d[12];
        int usbPid = d[13] | d[14] << 8;
        int rfByte = d[18];

        // 合理性校验：任何一项不合常理就整帧丢弃，宁可不显示也不显示错的
        if (percent > 100) return false;
        if (dpiX < 100 || dpiX > 32000) return false;
        if (dpiY < 100 || dpiY > 32000) return false;
        if (cfg > 15) return false;
        if (protocol > 3) return false;
        if (sensor > 15) return false;

        f = new StatusFrame
        {
            ReportId = d[0],
            Protocol = protocol,
            Sensor = sensor,
            CpiLevel = d[2],
            DpiX = dpiX,
            DpiY = dpiY,
            BatteryStatus = batStatus,
            Percent = percent,
            ConfigSlot = cfg,
            UsbPid = usbPid,
            RfConnected = (rfByte >> 7 & 1) == 1,
            Rssi = rfByte & 0x7F
        };
        return true;
    }
}

/// <summary>对外暴露的一台设备的当前状态。</summary>
internal sealed class DeviceView
{
    public string Key = "";
    public string Name = "无线外设";
    public string SourcePath = "";
    public int? Percent;
    public int BatteryStatus;
    public int DpiX, DpiY;
    public int Rssi;
    public int UsbPid;
    public DateTime LastSeen;
    public bool Stale;
    public string Note = "";
    /// <summary>给人看的一行明细，由各数据源自行拼装。</summary>
    public string DetailText = "";

    /// <summary>
    /// 状态字节 1 = 未充电（两次实测确认）。插上充电线时该字节变为其它值，
    /// 但具体哪个值对应充电/充满尚未验证，所以统一显示「充电中」而不细分。
    /// </summary>
    public string ChargeText => BatteryStatus switch
    {
        1 => "未充电",
        > 1 => "充电中",
        _ => "充电状态未知"
    };

    public string PercentText => Percent.HasValue ? $"{Percent}%" : "—";
}

/// <summary>
/// 设备监视：发现可解的厂商通道，起专用线程持续读取，对外发变更事件。
/// 全程只读：只调用 ReadFile 收设备主动上报的报文，不发送任何命令。
/// </summary>
internal sealed class DeviceMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, ChannelWorker> _workers = new();
    private readonly ConcurrentDictionary<string, DeviceView> _devices = new();
    private readonly System.Threading.Timer _discoveryTimer;
    private readonly System.Threading.Timer _staleTimer;
    private readonly object _gate = new();
    private volatile bool _disposed;
    private int _staleSeconds = 20;

    public event Action? Updated;

    public DeviceMonitor()
    {
        _discoveryTimer = new System.Threading.Timer(_ => SafeDiscover(), null, 0, 4000);
        _staleTimer = new System.Threading.Timer(_ => RefreshStale(), null, 1000, 1000);
    }

    public void SetStaleSeconds(int seconds) => _staleSeconds = Math.Clamp(seconds, 5, 600);

    public IReadOnlyList<DeviceView> Snapshot()
    {
        lock (_gate)
        {
            return _devices.Values
                .OrderByDescending(d => d.LastSeen)
                .Select(Clone)
                .ToList();
        }
    }

    public DeviceView? Primary()
    {
        var list = Snapshot().Where(d => d.Percent.HasValue).ToList();
        if (list.Count == 0) return null;
        // 单图标显示电量最低的那台：最需要关注的先看到
        return list.OrderBy(d => d.Percent!.Value).First();
    }

    private static DeviceView Clone(DeviceView d) => new()
    {
        Key = d.Key, Name = d.Name, SourcePath = d.SourcePath, Percent = d.Percent,
        BatteryStatus = d.BatteryStatus, DpiX = d.DpiX, DpiY = d.DpiY, Rssi = d.Rssi,
        UsbPid = d.UsbPid, LastSeen = d.LastSeen, Stale = d.Stale, Note = d.Note,
        DetailText = d.DetailText
    };

    private void SafeDiscover()
    {
        if (_disposed) return;
        try { Discover(); }
        catch (Exception ex) { Log.Warn("枚举设备失败: " + ex.Message); }
    }

    private void Discover()
    {
        var collections = Hid.Enumerate();

        // 同一物理设备里，鼠标/键盘等普通集合用于判断设备类型
        var byContainer = collections
            .GroupBy(c => c.ContainerKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var c in collections)
        {
            if (!c.IsVendor || c.InLen < 18 || c.InLen > 64 || !c.CanOpenRead) continue;
            if (_workers.ContainsKey(c.Path)) continue;

            string containerKey = c.ContainerKey;
            var peers = byContainer.TryGetValue(containerKey, out var list) ? list : new List<HidCollectionInfo>();
            string name = BuildName(c, peers);

            var worker = new ChannelWorker(c, containerKey, name, this);
            if (_workers.TryAdd(c.Path, worker)) worker.Start();
        }

        // 清理已拔掉的通道
        foreach (var kv in _workers.ToList())
        {
            if (collections.All(c => !string.Equals(c.Path, kv.Key, StringComparison.OrdinalIgnoreCase)))
            {
                if (_workers.TryRemove(kv.Key, out var w)) w.Dispose();
                lock (_gate) _devices.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string BuildName(HidCollectionInfo c, List<HidCollectionInfo> peers)
    {
        string brand = c.Vid switch
        {
            0x24AE => "雷柏",
            0x046D => "罗技",
            0x1532 => "雷蛇",
            _ => ""
        };

        string type = "";
        if (peers.Any(p => p.TopUsagePage == 0x01 && p.TopUsage == 0x02)) type = "无线鼠标";
        else if (peers.Any(p => p.TopUsagePage == 0x01 && p.TopUsage == 0x06)) type = "无线键盘";
        else if (c.BusReportedName.Contains("mouse", StringComparison.OrdinalIgnoreCase)) type = "无线鼠标";

        if (type.Length == 0)
        {
            string raw = FirstNonEmpty(c.BusReportedName, c.Product, "无线外设");
            return raw.Length > 28 ? raw[..28] : raw;
        }

        return brand.Length > 0 ? $"{brand} {type}" : type;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private void OnFrame(HidCollectionInfo collection, string containerKey, string name, StatusFrame frame)
    {
        bool changed = false;
        var view = _devices.GetOrAdd(collection.Path, _ => new DeviceView { Key = collection.Path });

        lock (_gate)
        {
            if (view.Name != name) { view.Name = name; changed = true; }
            if (view.Percent != frame.Percent)
            {
                Log.Info($"读数 {name}: {frame.Percent}%（{(view.Percent == null ? "首次" : $"上次 {view.Percent}")}）");
                view.Percent = frame.Percent;
                changed = true;
            }
            if (view.BatteryStatus != frame.BatteryStatus) { view.BatteryStatus = frame.BatteryStatus; changed = true; }
            if (view.DpiX != frame.DpiX || view.DpiY != frame.DpiY) { view.DpiX = frame.DpiX; view.DpiY = frame.DpiY; changed = true; }
            if (view.Rssi != frame.Rssi) { view.Rssi = frame.Rssi; changed = true; }
            if (view.UsbPid != frame.UsbPid) { view.UsbPid = frame.UsbPid; changed = true; }
            if (view.SourcePath != collection.ShortPath) { view.SourcePath = collection.ShortPath; changed = true; }
            if (view.Stale) { view.Stale = false; changed = true; }
            if (view.Note != "设备主动上报") { view.Note = "设备主动上报"; changed = true; }
            view.LastSeen = frame.Time;

            string detail = $"{view.ChargeText} · DPI {frame.DpiX}×{frame.DpiY} · 信号 {frame.Rssi}";
            if (view.DetailText != detail) { view.DetailText = detail; changed = true; }
        }

        if (changed) Updated?.Invoke();
    }

    private void RefreshStale()
    {
        if (_disposed) return;
        bool changed = false;
        lock (_gate)
        {
            foreach (var d in _devices.Values)
            {
                bool stale = (DateTime.Now - d.LastSeen).TotalSeconds > _staleSeconds;
                if (d.Stale != stale) { d.Stale = stale; changed = true; }
            }
        }
        if (changed) Updated?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _discoveryTimer.Dispose();
        _staleTimer.Dispose();
        foreach (var w in _workers.Values) w.Dispose();
        _workers.Clear();
    }

    /// <summary>一个通道一个专用线程：HID 句柄不能多线程并发读写。断线自动重连。</summary>
    private sealed class ChannelWorker : IDisposable
    {
        private readonly HidCollectionInfo _collection;
        private readonly string _containerKey;
        private readonly string _name;
        private readonly DeviceMonitor _owner;
        private readonly CancellationTokenSource _cts = new();
        private SafeFileHandle? _handle;
        private Thread? _thread;
        private int _validFrames;

        public ChannelWorker(HidCollectionInfo collection, string containerKey, string name, DeviceMonitor owner)
        {
            _collection = collection;
            _containerKey = containerKey;
            _name = name;
            _owner = owner;
        }

        public void Start()
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "hid-" + _collection.ShortPath
            };
            _thread.Start();
        }

        /// <summary>
        /// 设备休眠/切换会导致 ReadFile 失败，之前这里直接退出线程，
        /// 结果就是永久卡在"无响应"。现在改成同一个线程里重开句柄继续读。
        /// </summary>
        private void Loop()
        {
            int failures = 0;

            while (!_cts.IsCancellationRequested)
            {
                SafeFileHandle? handle = null;
                try
                {
                    handle = Hid.CreateFileW(_collection.Path, Hid.GENERIC_READ,
                        Hid.FILE_SHARE_READ | Hid.FILE_SHARE_WRITE, IntPtr.Zero, Hid.OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        failures++;
                        Log.Warn($"通道打不开 {_collection.ShortPath} 错误码 {err}，第 {failures} 次重试");
                        handle.Dispose();
                        handle = null;
                        if (_cts.Token.WaitHandle.WaitOne(Math.Min(60000, 3000 * failures))) break;
                        continue;
                    }

                    failures = 0;
                    _handle = handle;
                    var buffer = new byte[_collection.InLen];

                    while (!_cts.IsCancellationRequested)
                    {
                        if (!Hid.ReadFile(handle, buffer, buffer.Length, out int read, IntPtr.Zero))
                        {
                            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                            if (err != 995 && err != 6) // 995=主动取消 6=句柄已关
                                Log.Warn($"通道读取中断 {_collection.ShortPath} 错误码 {err}，准备重连");
                            break;
                        }
                        if (read <= 0) continue;

                        var frameBytes = new byte[read];
                        Buffer.BlockCopy(buffer, 0, frameBytes, 0, read);

                        if (!StatusDecoder.TryDecode(frameBytes, out var frame)) continue;

                        // 校验已经在 TryDecode 里做足（电量≤100、DPI 范围、配置槽范围），单帧即可采信。
                        // 设备空闲时上报间隔会在 3~9 秒之间浮动，等第二帧会让首次显示明显变慢。
                        _validFrames++;
                        _owner.OnFrame(_collection, _containerKey, _name, frame);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"通道读取异常 {_collection.ShortPath}: {ex.Message}");
                }
                finally
                {
                    if (ReferenceEquals(_handle, handle)) _handle = null;
                    try { handle?.Dispose(); } catch { }
                }

                if (_cts.Token.WaitHandle.WaitOne(2000)) break;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            // 线程可能正阻塞在 ReadFile 上，取消挂起 I/O 才能让它及时退出
            try { if (_handle is { IsInvalid: false }) Hid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
            try { if (_thread != null) _thread.Join(1500); } catch { }
            _cts.Dispose();
        }
    }
}
