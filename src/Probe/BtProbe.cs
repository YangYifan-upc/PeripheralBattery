using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace Probe;

/// <summary>
/// 蓝牙探测：列出已配对/已连接设备，检查 BLE 标准电池服务（0x180F / 0x2A19）。
/// 纯读取，不配对、不修改任何设备状态。
/// </summary>
internal static class BtProbe
{
    private static readonly Dictionary<ushort, string> KnownServices = new()
    {
        [0x1800] = "Generic Access",
        [0x1801] = "Generic Attribute",
        [0x180A] = "Device Information",
        [0x180F] = "★ Battery Service",
        [0x1812] = "HID over GATT",
        [0x1108] = "Audio Sink",
        [0x110A] = "Audio Source",
        [0x110B] = "A/V Remote Control",
        [0x110E] = "Handsfree",
        [0x1112] = "Headset",
        [0x111E] = "Handsfree Audio Gateway",
        [0x111F] = "Headset Audio Gateway"
    };

    public static async Task<string> RunAsync()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L($"PeripheralBattery 蓝牙探测   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L("================================================================");

        await DumpClassicAsync(L);
        await DumpBleAsync(L);
        await DumpAudioAsync(L);

        L("===== 判读说明 =====");
        L("只有出现「★ Battery Service」并成功读到 Battery Level 的设备，才能显示电量。");
        L("经典蓝牙（音频）设备即使已连接，也可能完全不暴露电池服务。");
        return sb.ToString();
    }

    private static async Task DumpClassicAsync(Action<string> L)
    {
        L("---- 一、经典蓝牙（音频/输入设备）----");
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector());
            L($"已配对/已知设备 {infos.Count} 个");
            foreach (var info in infos)
            {
                BluetoothDevice? bt = null;
                try
                {
                    bt = await BluetoothDevice.FromIdAsync(info.Id);
                }
                catch (Exception ex)
                {
                    L($"  [{info.Name}] 打不开: {ex.GetType().Name}");
                    continue;
                }

                if (bt == null) { L($"  [{info.Name}] FromIdAsync 返回空"); continue; }

                L($"  · {bt.Name}");
                L($"    地址 {FormatAddress(bt.BluetoothAddress)}   状态 {bt.ConnectionStatus}");
                L($"    设备类别 {bt.ClassOfDevice.MajorClass}/{bt.ClassOfDevice.MinorClass}  " +
                  $"（音频类={bt.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo}）");
                L($"    DeviceId {info.Id}");

                try
                {
                    var rfcomm = await bt.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                    if (rfcomm.Services.Count > 0)
                    {
                        var list = rfcomm.Services.Select(s => $"{s.ServiceId.Uuid}").Distinct().Take(8);
                        L($"    RFCOMM 服务: {string.Join(", ", list)}");
                    }
                    else
                    {
                        L("    RFCOMM 服务: 未取到");
                    }
                }
                catch (Exception ex)
                {
                    L($"    RFCOMM 枚举失败: {ex.GetType().Name} — {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            L($"  经典蓝牙枚举失败: {ex.GetType().Name} — {ex.Message}");
        }
        L(string.Empty);
    }

    private static async Task DumpBleAsync(Action<string> L)
    {
        L("---- 二、低功耗蓝牙（BLE）与 GATT 服务 ----");
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
            L($"BLE 设备 {infos.Count} 个");
            foreach (var info in infos)
            {
                BluetoothLEDevice? le = null;
                try
                {
                    le = await BluetoothLEDevice.FromIdAsync(info.Id);
                }
                catch (Exception ex)
                {
                    L($"  [{info.Name}] 打不开: {ex.GetType().Name}");
                    continue;
                }

                if (le == null) { L($"  [{info.Name}] FromIdAsync 返回空"); continue; }

                L($"  · {le.Name}   地址 {FormatAddress(le.BluetoothAddress)}   连接 {le.ConnectionStatus}");

                GattDeviceServicesResult? result = null;
                for (int attempt = 1; attempt <= 2 && result == null; attempt++)
                {
                    try
                    {
                        result = await le.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    }
                    catch (Exception ex)
                    {
                        L($"    第 {attempt} 次取 GATT 失败: {ex.GetType().Name} — {ex.Message}");
                        await Task.Delay(500);
                    }
                }

                if (result == null) { L("    无法读取 GATT 服务"); continue; }
                if (result.Status != GattCommunicationStatus.Success)
                {
                    L($"    GATT 状态: {result.Status}");
                    continue;
                }

                L($"    服务 {result.Services.Count} 个:");
                foreach (var service in result.Services)
                {
                    ushort shortId = ToShortUuid(service.Uuid);
                    string name = KnownServices.TryGetValue(shortId, out var n) ? n : "未知服务";
                    L($"      0x{shortId:X4}  {name}");

                    if (shortId != 0x180F) continue;
                    await ReadBatteryAsync(L, service);
                }
            }
        }
        catch (Exception ex)
        {
            L($"  BLE 枚举失败: {ex.GetType().Name} — {ex.Message}");
        }
        L(string.Empty);
    }

    private static async Task ReadBatteryAsync(Action<string> L, GattDeviceService service)
    {
        try
        {
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            foreach (var c in chars.Characteristics)
            {
                L($"        特征 0x{ToShortUuid(c.Uuid):X4}  属性 {c.CharacteristicProperties}");
                if (ToShortUuid(c.Uuid) != 0x2A19) continue;

                var read = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success)
                {
                    L($"        ✗ 读电量失败: {read.Status}");
                    continue;
                }

                var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
                byte level = reader.ReadByte();
                L($"        ✓ 电量 = {level}%");
            }
        }
        catch (Exception ex)
        {
            L($"        ✗ 读取特征失败: {ex.GetType().Name} — {ex.Message}");
        }
    }

    private static async Task DumpAudioAsync(Action<string> L)
    {
        L("---- 三、系统音频设备（确认耳机是否已连接）----");
        try
        {
            var infos = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
            foreach (var info in infos)
            {
                bool enabled = info.IsEnabled;
                L($"  · {info.Name}  可用={enabled}");
            }
        }
        catch (Exception ex)
        {
            L($"  音频设备枚举失败: {ex.GetType().Name}");
        }
        L(string.Empty);
    }

    private static ushort ToShortUuid(Guid uuid)
    {
        // 蓝牙短 UUID：0000xxxx-0000-1000-8000-00805F9B34FB
        byte[] b = uuid.ToByteArray();
        return (ushort)(b[2] | b[3] << 8);
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(x => x.ToString("X2")));
    }
}
