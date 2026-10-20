using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Probe;

/// <summary>
/// 解码验证：监听厂商 input 通道，按网页驱动里的解析函数还原字段。
/// 纯读取，不发送任何命令。
/// </summary>
internal static class DecodeProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    private static string Hex(byte[] d, int max) => HidProbe.Hex(d, max);

    public static string Run(int seconds)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L($"PeripheralBattery Decode Probe   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"只读监听 {seconds}s；按 web 驱动的解析规则还原字段");
        L("================================================================");

        var interfaces = HidProbe.Enumerate().Where(i => i.Vid == 0x24AE && i.TopUsagePage >= 0xFF00).ToList();
        var listeners = new List<(RawInterface itf, SafeFileHandle h, Thread t, List<string> log, HashSet<string> seen)>();

        foreach (var itf in interfaces.Where(i => i.InLen > 0 && i.HasReadAccess))
        {
            var h = HidNative.CreateFileW(itf.Path, HidNative.GENERIC_READ,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { L($"[{itf.ShortPath}] 打不开 {Marshal.GetLastWin32Error()}"); continue; }

            var log = new List<string>();
            var seen = new HashSet<string>();
            var itfCopy = itf;
            var thread = new Thread(() =>
            {
                var buf = new byte[itfCopy.InLen];
                while (true)
                {
                    try
                    {
                        if (!ReadFile(h, buf, buf.Length, out int read, IntPtr.Zero)) break;
                        if (read <= 0) continue;
                        var copy = buf[..read];
                        string key = Hex(copy, read);
                        lock (log)
                        {
                            if (seen.Add(key) && log.Count < 40)
                                log.Add($"{DateTime.Now:HH:mm:ss} {key}\n      → {Decode(copy)}");
                        }
                    }
                    catch { break; }
                }
            }) { IsBackground = true };
            listeners.Add((itf, h, thread, log, seen));
            thread.Start();
        }

        L($"已监听 {listeners.Count} 个通道");
        Thread.Sleep(seconds * 1000);
        foreach (var l in listeners) CancelIoEx(l.h, IntPtr.Zero);
        foreach (var l in listeners) l.t.Join(1500);

        L();
        foreach (var l in listeners)
        {
            L($"[{l.itf.ShortPath}] 0x{l.itf.TopUsagePage:X2}:0x{l.itf.TopUsage:X2}  In={l.itf.InLen}  不同报文 {l.seen.Count} 种");
            lock (l.log)
            {
                foreach (var line in l.log) L("    " + line);
                if (l.log.Count == 0) L("    （没有收到报文）");
            }
            L();
        }

        foreach (var l in listeners) l.h.Dispose();
        return sb.ToString();
    }

    /// <summary>
    /// 按 bundle 里 VTLM20RTMouseDevice.parseUserCustomData 的规则解码。
    /// 入参含首字节 report id，故下标整体 +1。
    /// </summary>
    private static string Decode(byte[] d)
    {
        if (d.Length < 18)
            return $"长度 {d.Length}，小于 18，不符合鼠标状态帧";

        int e0 = d[1];                      // conProtocol | sensor
        int xCpi = d[3] | d[4] << 8;
        int yCpi = d[5] | d[6] << 8;
        int batStatus = d[7];
        int batPercent = d[8];
        int cfgSel = d[12];
        int usbPid = d[13] | d[14] << 8;
        int rfRssi = d.Length > 18 ? d[18] & 0x7F : -1;
        bool rfConnected = d.Length > 18 && (d[18] >> 7 & 1) == 1;

        string warn = "";
        if (batPercent > 100) warn = "  ⚠ 电量越界，不可信";
        else if (batPercent == 0xFF) warn = "  ⚠ 0xFF 表示无效";

        return $"电量 {batPercent}%  充电状态 {batStatus}  DPI {xCpi}x{yCpi}  " +
               $"配置槽 {cfgSel}  协议 {(e0 & 15)}  传感器 {(e0 >> 4)}  " +
               $"usbPid 0x{usbPid:X4}  连接 {(rfConnected ? "是" : "否")} RSSI {rfRssi}{warn}";
    }
}
