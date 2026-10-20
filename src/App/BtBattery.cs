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
/// 已验证：第 [6] 字节的高/低半字节 = 两只耳机电池，10% 步长；与 iPhone 显示一致。
/// 未采用：第 [7] 字节高半字节解析出来是 80%，而 iPhone 同期显示 66%——对不上，
///        因此充电盒电量不显示，宁可空着也不给错数。
///
/// 局限：广播不含设备唯一身份（蓝牙地址每轮随机），附近有多副同款时无法精确区分，
/// 只能锁定信号最强的那副；界面标注"蓝牙广播"，信号强度一并显示供核对。
/// </summary>
internal sealed class BtBatteryWatcher : IDisposable
{
    private const short MinRssi = -70;        // 更弱的多半是邻居的设备
    private const int StaleSeconds = 150;     // 耳机合盖后停止广播，给足宽限
    private const int LockSeconds = 20;       // 锁定当前设备的时间，避免在几副耳机间跳

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly DeviceMonitor _sink;
    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly HashSet<int> _publishedModels = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _sweepTimer;

    private ulong? _lockedAddress;
    private DateTime _lockedAt = DateTime.MinValue;

    private sealed class Entry
    {
        public ulong Address;
        public int Model;
        public int BudA = 0x0F, BudB = 0x0F;
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
        _sweepTimer = new System.Threading.Timer(_ => { try { Sweep(); } catch { } }, null, 10000, 10000);
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
                if (budA > 10 && budB > 10) continue;   // 两只都没数据，跳过

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
                    entry.Rssi = args.RawSignalStrengthInDBm;
                    entry.LastSeen = DateTime.Now;

                    keep = PickLocked();
                }

                if (keep != null) Publish(keep);
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
        detail.Append($" · 蓝牙广播 · 信号 {entry.Rssi}");

        var view = new DeviceView
        {
            Key = $"bt:{entry.Model}",
            Name = $"{ModelName(entry.Model)}（蓝牙）",
            SourcePath = "bluetooth-advertisement",
            Percent = percent,
            LastSeen = DateTime.Now,
            DetailText = detail.ToString(),
            SubValues = new List<(string, int)> { ("耳机", percent) }
        };

        lock (_gate)
        {
            if (_publishedModels.Add(entry.Model))
                Log.Info($"蓝牙设备已加入面板: 0x{entry.Model:X4}");
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

            // 某个机型彻底没有广播了，才从面板移除
            var liveModels = _entries.Values.Select(e => e.Model).ToHashSet();
            foreach (int model in _publishedModels.Where(m => !liveModels.Contains(m)).ToList())
            {
                _publishedModels.Remove(model);
                _sink.RemoveExternal($"bt:{model}");
                Log.Info($"蓝牙设备已离开: 0x{model:X4}");
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
        try
        {
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
            _watcher.Received -= OnReceived;
        }
        catch { }
    }
}
