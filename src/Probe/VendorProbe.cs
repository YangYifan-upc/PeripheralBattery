using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Probe;

/// <summary>
/// 第二阶段：对厂商私有集合做「纯读取」观察。
/// 仍然零写入：只调用 HidD_GetFeature（读 feature 报告）与 ReadFile（收 input 报告）。
/// 本文件同样不声明 HidD_SetFeature / WriteFile。
/// 所有可读接口并行监听同一个时间窗，避免串行等待。
/// </summary>
internal static class VendorProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buffer, int numberOfBytesToRead,
        out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    private sealed class Listener
    {
        public RawInterface Itf = null!;
        public SafeFileHandle Handle = null!;
        public Thread Thread = null!;
        public int Count;
        public readonly List<string> Samples = new();
        public readonly List<string> Distinct = new();
        public readonly List<string> Errors = new();
        public byte[] Min = Array.Empty<byte>();
        public byte[] Max = Array.Empty<byte>();
    }

    public static string Run(int seconds)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L($"PeripheralBattery Vendor Read-Only Probe   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"模式: 只读（HidD_GetFeature + ReadFile），并行监听 {seconds}s");
        L("================================================================");

        var interfaces = HidProbe.Enumerate().Where(i => i.Vid == 0x24AE).ToList();
        L($"雷柏接口数: {interfaces.Count}");
        L();

        L("---- 一、Feature 报告读取（请求一次、读一次，不发任何命令）----");
        foreach (var itf in interfaces.Where(i => i.FeatLen > 0))
        {
            L($"[{itf.ShortPath}] 顶层 0x{itf.TopUsagePage:X2}:0x{itf.TopUsage:X2} Feat={itf.FeatLen} 访问={itf.Access}");
            FeatureReads(L, itf);
        }

        L();
        L($"---- 二、Input 报告并行监听 {seconds}s ----");
        var listeners = new List<Listener>();
        foreach (var itf in interfaces.Where(i => i.InLen > 0 && i.HasReadAccess))
        {
            var handle = HidNative.CreateFileW(itf.Path,
                HidNative.GENERIC_READ, HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                L($"[{itf.ShortPath}] 打不开 (错误码 {Marshal.GetLastWin32Error()})");
                handle.Dispose();
                continue;
            }

            var listener = new Listener
            {
                Itf = itf,
                Handle = handle,
                Min = Enumerable.Repeat((byte)255, itf.InLen).ToArray(),
                Max = new byte[itf.InLen]
            };
            listener.Thread = new Thread(() => ReadLoop(listener)) { IsBackground = true, Name = "hid-" + itf.ShortPath };
            listeners.Add(listener);
            listener.Thread.Start();
        }

        L($"已启动 {listeners.Count} 个监听线程");
        Thread.Sleep(seconds * 1000);

        foreach (var l in listeners) CancelIoEx(l.Handle, IntPtr.Zero);
        foreach (var l in listeners) l.Thread.Join(2000);

        L();
        foreach (var l in listeners)
        {
            L($"[{l.Itf.ShortPath}] 顶层 0x{l.Itf.TopUsagePage:X2}:0x{l.Itf.TopUsage:X2} In={l.Itf.InLen} 收到 {l.Count} 条");
            lock (l.Samples)
            {
                foreach (var s in l.Samples.Take(6)) L("    " + s);
                if (l.Count == 0) L("    （没有收到任何报文）");
                foreach (var e in l.Errors.Take(2)) L("    ! " + e);
                var changed = ChangedOffsets(l);
                if (changed.Count > 0) L("    字节变化范围: " + changed);
            }
            L();
        }

        L("===== 判定辅助 =====");
        L("若某个集合持续收到报文且其中某个字节缓慢变化，则该字节是最可能的电量字段候选，");
        L("但必须满足：随使用单调下降、至少 3 个电量点与官方软件/指示灯吻合、值域 0-100，才允许显示为百分比。");
        L("若所有集合都收不到报文，说明设备只在被主动查询时才回复，必须抓包确认命令格式。");

        foreach (var l in listeners) l.Handle.Dispose();
        return sb.ToString();
    }

    private static void ReadLoop(Listener l)
    {
        var buf = new byte[l.Itf.InLen];
        var localMin = l.Min;
        var localMax = l.Max;
        while (true)
        {
            try
            {
                if (!ReadFile(l.Handle, buf, buf.Length, out int read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 995 /* ERROR_OPERATION_ABORTED */ && err != 6 /* ERROR_INVALID_HANDLE */)
                        lock (l.Errors) l.Errors.Add($"ReadFile 失败 err={err}");
                    break;
                }
                if (read <= 0) continue;

                Interlocked.Increment(ref l.Count);
                string hex = HidProbe.Hex(buf, Math.Min(read, 40));
                lock (l.Samples)
                {
                    if (l.Samples.Count < 40) l.Samples.Add($"{DateTime.Now:HH:mm:ss.fff} len={read} {hex}");
                    if (l.Distinct.Count < 10 && !l.Distinct.Contains(hex)) l.Distinct.Add(hex);
                    for (int i = 0; i < read && i < localMin.Length; i++)
                    {
                        if (buf[i] < localMin[i]) localMin[i] = buf[i];
                        if (buf[i] > localMax[i]) localMax[i] = buf[i];
                    }
                }
            }
            catch (Exception ex)
            {
                lock (l.Errors) l.Errors.Add(ex.GetType().Name + ": " + ex.Message);
                break;
            }
        }
    }

    private static List<string> ChangedOffsets(Listener l)
    {
        var result = new List<string>();
        int n = Math.Min(l.Min.Length, 32);
        for (int i = 0; i < n; i++)
        {
            if (l.Min[i] != l.Max[i])
                result.Add($"[{i}]={l.Min[i]}..{l.Max[i]}");
        }
        return result;
    }

    private static void FeatureReads(Action<string> L, RawInterface itf)
    {
        var reportIds = itf.FeatureCaps.Select(c => c.ReportId).Distinct().OrderBy(x => x).ToList();
        if (reportIds.Count == 0) reportIds.Add(0);

        using var handle = HidNative.CreateFileW(itf.Path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            L($"    Feature 读取: 打不开 (错误码 {Marshal.GetLastWin32Error()})");
            return;
        }

        foreach (byte rid in reportIds)
        {
            var buf = new byte[itf.FeatLen];
            buf[0] = rid;
            bool ok = HidNative.HidD_GetFeature(handle, buf, buf.Length);
            L(ok
                ? $"    Feature RID=0x{rid:X2} 读到: {HidProbe.Hex(buf, 33)}"
                : $"    Feature RID=0x{rid:X2} 失败 (错误码 {Marshal.GetLastWin32Error()})");
        }
    }
}
