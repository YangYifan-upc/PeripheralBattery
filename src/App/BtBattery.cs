using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace PeripheralBattery;

/// <summary>
/// 蓝牙耳机电量：被动监听苹果「Proximity Pairing」广播（厂商数据 0x004C，类型 0x07）。
/// 广播明文、无需连接，不需配对，也不改设备状态。
///
/// 为什么不走别的路：
/// - BLE 标准电池服务 0x180F：AirPods 不提供，实测 GATT 里没有
/// - 苹果私有 L2CAP 协议：要模拟 iPhone 握手，工作量大且不稳定
///
/// 已验证：
/// - 第 [6] 字节高/低半字节 = 两只耳机电池，10% 步长，与 iPhone 显示一致
/// - 第 [7] 字节**低**半字节 = 充电盒电池，同样是 10% 一档且向下取整
///   （实测两次：iPhone 显示 66% 时该值为 6，显示 59% 时该值为 5），
///   因此充电盒只能给出「50–59%」这样的区间，不显示精确值。
///   第 [7] 字节高半字节含义未知（实测恒为 11，超出电量范围），未使用。
///
/// 局限：广播不含设备唯一身份（蓝牙地址每轮随机），附近有多副同款时无法精确区分，
/// 只能锁定信号最强的那副；界面标注"蓝牙广播"，信号强度一并显示供核对。
/// </summary>

/// <summary>
/// 判断"本机是否连着一副苹果耳机"。广播地址每轮随机，无法与已配对设备精确对应
/// （那需要 IRK 密钥，属系统级数据），因此改用这条连接状态作为闸门：
/// 只有电脑确实连着 AirPods 时才展示广播电量，避免在你没连耳机时显示隔壁的耳机。
/// </summary>
internal sealed class BtLinkState : IDisposable
{
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private bool _connected;
    private string _name = "";
    private bool _refreshing;

    public BtLinkState()
    {
        _timer = new System.Threading.Timer(_ => Refresh(), null, 0, 5000);
    }

    public bool IsConnected { get { lock (_gate) return _connected; } }
    public string DeviceName { get { lock (_gate) return _name; } }

    private async void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            string found = "";
            var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelector());

            foreach (var info in infos)
            {
                if (!LooksLikeAppleAudio(info.Name)) continue;
                BluetoothDevice? bt = null;
                try { bt = await BluetoothDevice.FromIdAsync(info.Id); } catch { }
                if (bt?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    found = CleanName(bt.Name);
                    break;
                }
            }

            bool changed;
            lock (_gate)
            {
                changed = _connected != (found.Length > 0) || _name != found;
                _connected = found.Length > 0;
                _name = found;
            }
            if (changed) Log.Info(found.Length > 0 ? $"蓝牙耳机已连接: {found}" : "蓝牙耳机未连接，暂停展示广播电量");
        }
        catch (Exception ex)
        {
            Log.Warn("查询蓝牙连接状态失败: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static bool LooksLikeAppleAudio(string name) =>
        name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Beats", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HomePod", StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string name) =>
        name.Replace(" - Find My", "", StringComparison.OrdinalIgnoreCase).Trim();

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
    }
}

internal sealed class BtBatteryWatcher : IDisposable
{
    private const short MinRssi = -60;        // 更弱的多半是邻居的设备，宁可少列也不列错
    private const int StaleSeconds = 150;     // 耳机合盖后停止广播，给足宽限
    private const int LockSeconds = 20;       // 锁定当前设备的时间，避免在几副耳机间跳

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly DeviceMonitor _sink;
    private readonly Dictionary<ulong, Entry> _entries = new();
    private int _publishedKey = -1;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _sweepTimer;
    private readonly BtLinkState _link;

    private ulong? _lockedAddress;
    private DateTime _lockedAt = DateTime.MinValue;

    private sealed class Entry
    {
        public ulong Address;
        public int Model;
        public int BudA = 0x0F, BudB = 0x0F;
        public int CaseNibble = 0x0F;
        public short Rssi;
        public DateTime LastSeen = DateTime.Now;
    }

    public BtBatteryWatcher(DeviceMonitor sink)
    {
        _sink = sink;
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += OnReceived;
        _link = new BtLinkState();
        _sweepTimer = new System.Threading.Timer(_ => { try { Sweep(); } catch { } }, null, 5000, 5000);
    }

    public void Start()
    {
        try
        {
            _watcher.Start();
            Log.Info("蓝牙广播监听已启动");
        }
        catch (Exception ex)
        {
            Log.Warn("蓝牙广播监听启动失败: " + ex.Message);
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != 0x004C) continue;

                var data = new byte[md.Data.Length];
                using (var reader = DataReader.FromBuffer(md.Data)) reader.ReadBytes(data);

                // 0x07 = Proximity Pairing（苹果耳机开盖/连接时广播）
                if (data.Length < 10 || data[0] != 0x07) continue;
                if (args.RawSignalStrengthInDBm < MinRssi) continue;

                int model = data[3] << 8 | data[4];
                int budA = data[6] >> 4;
                int budB = data[6] & 0x0F;
                int caseNibble = data[7] & 0x0F;
                if (budA > 10 && budB > 10 && caseNibble > 10) continue;

                Entry? keep = null;
                lock (_gate)
                {
                    if (!_entries.TryGetValue(args.BluetoothAddress, out var entry))
                    {
                        entry = new Entry { Address = args.BluetoothAddress, Model = model };
                        _entries[args.BluetoothAddress] = entry;
                        Log.Info($"发现蓝牙耳机广播 0x{model:X4}（{FormatAddress(args.BluetoothAddress)}）");
                    }
                    entry.BudA = budA;
                    entry.BudB = budB;
                    entry.CaseNibble = caseNibble;
                    entry.Rssi = args.RawSignalStrengthInDBm;
                    entry.LastSeen = DateTime.Now;

                    keep = PickLocked();
                }

                // 只有本机确实连着苹果耳机时才展示；否则可能是在显示隔壁工位那副
                if (keep != null && _link.IsConnected) Publish(keep);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("解析蓝牙广播失败: " + ex.Message);
        }
    }

    /// <summary>挑选当前代表设备：优先保持已锁定的那副，避免几副同款耳机来回抢占显示。</summary>
    private Entry? PickLocked()
    {
        var fresh = _entries.Values
            .Where(e => (DateTime.Now - e.LastSeen).TotalSeconds <= 10)
            .OrderByDescending(e => e.Rssi)
            .ToList();
        if (fresh.Count == 0) return null;

        var best = fresh[0];
        if (_lockedAddress is { } locked &&
            (DateTime.Now - _lockedAt).TotalSeconds < LockSeconds &&
            _entries.TryGetValue(locked, out var kept) &&
            (DateTime.Now - kept.LastSeen).TotalSeconds <= 10 &&
            best.Rssi - kept.Rssi <= 12)
        {
            return kept;
        }

        if (_lockedAddress != best.Address)
        {
            _lockedAddress = best.Address;
            _lockedAt = DateTime.Now;
            Log.Info($"锁定蓝牙设备 {FormatAddress(best.Address)}，信号 {best.Rssi} dBm");
        }

        return best;
    }

    private void Publish(Entry entry)
    {
        bool both = entry.BudA <= 10 && entry.BudB <= 10;
        int percent = both ? Math.Min(entry.BudA, entry.BudB) * 10
                    : entry.BudA <= 10 ? entry.BudA * 10
                    : entry.BudB * 10;

        var detail = new StringBuilder();
        if (both)
            detail.Append(entry.BudA == entry.BudB
                ? $"耳机 {entry.BudA * 10}%（两只）"
                : $"耳机 {entry.BudA * 10}% / {entry.BudB * 10}%");
        else
            detail.Append($"耳机 {percent}%");

        // 充电盒电量：实测为第 7 字节的低半字节，10% 一档（向下取整），只能给区间
        if (entry.CaseNibble <= 10)
            detail.Append($" · 盒 {entry.CaseNibble * 10}–{entry.CaseNibble * 10 + 9}%");

        detail.Append($" · 蓝牙广播 · 信号 {entry.Rssi}");

        var view = new DeviceView
        {
            Key = "bt:active",
            Name = _link.DeviceName is { Length: > 0 } n ? n : $"{ModelName(entry.Model)}（蓝牙）",
            SourcePath = "bluetooth-advertisement",
            Percent = percent,
            LastSeen = DateTime.Now,
            DetailText = detail.ToString(),
            SubValues = new List<(string, int)> { ("耳机", percent) }
        };

        lock (_gate)
        {
            if (_publishedKey != entry.Model)
            {
                _publishedKey = entry.Model;
                Log.Info($"蓝牙设备已加入面板: 0x{entry.Model:X4}");
            }
        }

        _sink.PublishExternal(view, TimeSpan.FromSeconds(StaleSeconds));
    }

    /// <summary>机型名只收录能确认的；不确定的显示型号编号，不猜。</summary>
    private static string ModelName(int model) => model switch
    {
        0x0E20 => "AirPods Pro",
        _ => $"Apple 耳机 0x{model:X4}"
    };

    private void Sweep()
    {
        lock (_gate)
        {
            foreach (var address in _entries.Keys.ToList())
            {
                if ((DateTime.Now - _entries[address].LastSeen).TotalSeconds > StaleSeconds)
                {
                    _entries.Remove(address);
                    if (_lockedAddress == address) _lockedAddress = null;
                }
            }

            // 断开连接或一副都收不到了，就从面板移除
            if ((_entries.Count == 0 || !_link.IsConnected) && _publishedKey != -1)
            {
                _publishedKey = -1;
                _sink.RemoveExternal("bt:active");
                Log.Info("蓝牙耳机已无广播，从面板移除");
            }
        }
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(x => x.ToString("X2")));
    }

    public void Dispose()
    {
        try { _sweepTimer.Dispose(); } catch { }
        try { _link.Dispose(); } catch { }
        try
        {
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
            _watcher.Received -= OnReceived;
        }
        catch { }
    }
}
