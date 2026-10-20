using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Probe;

/// <summary>
/// 抓蓝牙广播里的苹果「Proximity Pairing」包（Apple 厂商数据 0x004C，类型 0x07）。
/// 这类广播是明文且无需连接的，里面带左右耳与充电盒电量。
/// 纯被动监听，不连接、不配对、不修改设备。
/// </summary>
internal static class BtScanProbe
{
    private sealed class Hit
    {
        public ulong Address;
        public short Rssi;
        public readonly List<string> Packets = new();   // 全部不同报文，用于对照
    }

    public static async Task<string> RunAsync(int seconds)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L($"PeripheralBattery 蓝牙广播扫描   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"主动扫描 {seconds} 秒，只解析苹果厂商数据（Company ID 0x004C）");
        L("================================================================");

        var hits = new Dictionary<string, Hit>();
        int totalAds = 0, appleAds = 0, pairingAds = 0;
        var gate = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, e) =>
        {
            lock (gate)
            {
                totalAds++;
                foreach (var md in e.Advertisement.ManufacturerData)
                {
                    if (md.CompanyId != 0x004C) continue;
                    appleAds++;

                    var data = new byte[md.Data.Length];
                    using (var reader = DataReader.FromBuffer(md.Data))
                        reader.ReadBytes(data);

                    if (data.Length == 0) continue;
                    string hex = Convert.ToHexString(data);

                    // 类型 0x07 = Proximity Pairing（苹果耳机开盖广播）
                    if (data[0] != 0x07) continue;
                    pairingAds++;

                    string key = Convert.ToString((long)e.BluetoothAddress, 16);
                    if (!hits.TryGetValue(key, out var hit))
                    {
                        hit = new Hit { Address = e.BluetoothAddress, Rssi = e.RawSignalStrengthInDBm };
                        hits[key] = hit;
                    }
                    hit.Rssi = e.RawSignalStrengthInDBm;

                    string line = $"{DateTime.Now:HH:mm:ss}  {hex}  →  {DecodePairing(data)}";
                    if (!hit.Packets.Any(p => p.EndsWith(hex)))     // 同一报文只记一次
                        hit.Packets.Add(line);
                }
            }
        };

        watcher.Stopped += (_, args) =>
        {
            if (args.Error != BluetoothError.Success)
                Console.Error.WriteLine("扫描器停止，错误: " + args.Error);
        };

        watcher.Start();
        await Task.Delay(seconds * 1000);
        watcher.Stop();

        lock (gate)
        {
            L($"累计广播 {totalAds} 条，其中苹果厂商数据 {appleAds} 条，Proximity Pairing {pairingAds} 条");
            L(string.Empty);

            if (hits.Count == 0)
            {
                L("✗ 没有抓到 Proximity Pairing 广播。");
                L("  可能原因：");
                L("  1) 耳机正在与电脑播放音频，被占用后不再广播（最常见）");
                L("  2) 充电盒盖着（盖着时停止广播）");
                L("  3) 耳机离得较远或电量耗尽");
                L("  建议：打开盒盖、把耳机放在电脑旁、暂停播放，再扫一次");
            }
            else
            {
                foreach (var h in hits.Values.OrderByDescending(x => x.Rssi))
                {
                    L($"· 地址 {FormatAddress(h.Address)}  RSSI {h.Rssi} dBm   共 {h.Packets.Count} 种报文");
                    foreach (var p in h.Packets.Take(10)) L("    " + p);
                    L(string.Empty);
                }

                L("提示：电池值以 10 为步长编码（0–10 对应 0–100%），0x0F 表示该项无数据。");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 苹果 Proximity Pairing 包结构（按实测数据校正）：
    /// [0]=0x07 类型, [1]=长度(0x19), [2]=0x01 前缀, [3..4]=机型(大端),
    /// [5]=状态位, [6] 高/低半字节=两只耳机电量, [7] 高半字节=充电盒电量、低半字节=充电标志。
    /// 电量以 10 为步长（0–10 → 0–100%），0x0F 表示该项无数据。
    /// </summary>
    private static string DecodePairing(byte[] data)
    {
        if (data.Length < 10) return $"有效长度不足（{data.Length} 字节）";

        int model = data[3] << 8 | data[4];
        byte status = data[5];
        int budA = data[6] >> 4;
        int budB = data[6] & 0x0F;
        int caseBattery = data[7] >> 4;
        int chargeFlags = data[7] & 0x0F;

        string Battery(int nibble) => nibble <= 10 ? $"{nibble * 10}%" : "—";

        return $"机型 0x{model:X4}（{ModelName(model)}）  状态 0x{status:X2}  " +
               $"耳机1 {Battery(budA)}  耳机2 {Battery(budB)}  盒 {Battery(caseBattery)}  " +
               $"充电标志 0x{chargeFlags:X1}";
    }

    private static string ModelName(int model) => model switch
    {
        0x0E20 => "AirPods Pro",
        _ => "未收录"
    };

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(x => x.ToString("X2")));
    }
}
