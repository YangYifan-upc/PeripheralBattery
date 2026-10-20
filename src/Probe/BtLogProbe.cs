using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Probe;

/// <summary>
/// 长时间记录苹果 Proximity Pairing 广播到 CSV，用于「哪个字节是充电盒电量」的对照实验。
/// 只记录与上一条不同的报文，文件不会膨胀。
/// </summary>
internal static class BtLogProbe
{
    public static async Task<string> RunAsync(int seconds, string csvPath)
    {
        var seen = new Dictionary<string, string>();   // 地址 → 上一条报文
        int total = 0, written = 0;
        var gate = new object();

        using var writer = new StreamWriter(csvPath, append: false, new UTF8Encoding(false));
        writer.WriteLine("时间,地址,信号,机型,状态,耳1,耳2,字节7高,字节7低,字节8,字节9,字节10,字节11,原始数据");

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, e) =>
        {
            lock (gate)
            {
                total++;
                foreach (var md in e.Advertisement.ManufacturerData)
                {
                    if (md.CompanyId != 0x004C) continue;

                    var data = new byte[md.Data.Length];
                    using (var reader = DataReader.FromBuffer(md.Data)) reader.ReadBytes(data);
                    if (data.Length < 10 || data[0] != 0x07) continue;

                    string hex = Convert.ToHexString(data);
                    string addr = FormatAddress(e.BluetoothAddress);
                    if (seen.TryGetValue(addr, out var last) && last == hex) continue;
                    seen[addr] = hex;
                    written++;

                    int model = data[3] << 8 | data[4];
                    writer.WriteLine(string.Join(',',
                        DateTime.Now.ToString("HH:mm:ss.fff"),
                        addr,
                        e.RawSignalStrengthInDBm,
                        $"0x{model:X4}",
                        $"0x{data[5]:X2}",
                        data[6] >> 4,
                        data[6] & 0x0F,
                        data[7] >> 4,
                        data[7] & 0x0F,
                        $"0x{data[8]:X2}",
                        $"0x{data[9]:X2}",
                        $"0x{data[10]:X2}",
                        $"0x{data[11]:X2}",
                        hex));
                    writer.Flush();
                }
            }
        };

        watcher.Start();
        Console.WriteLine($"开始记录 {seconds} 秒 → {csvPath}");
        await Task.Delay(seconds * 1000);
        watcher.Stop();

        lock (gate)
        {
            writer.Flush();
        }

        return $"记录结束：收到广播 {total} 条，写入变化报文 {written} 条\n文件：{csvPath}";
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(x => x.ToString("X2")));
    }
}
