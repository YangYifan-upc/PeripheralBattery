using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Probe;

/// <summary>
/// 对照实验用监视器：只读轮询厂商 feature 报告 + 可选并行监听 input 报告。
/// 不发送任何命令，不影响正在运行的官方驱动。
/// </summary>
internal static class WatchProbe
{
    public static string Run(int seconds, bool listenInput)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("================================================================");
        L($"PeripheralBattery Watch Probe   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        L($"模式: 只读轮询 feature + {(listenInput ? "监听 input" : "不碰 input（避免抢官方驱动的报文）")}，时长 {seconds}s");
        L("================================================================");

        var interfaces = HidProbe.Enumerate().Where(i => i.Vid == 0x24AE).ToList();
        var targets = interfaces.Where(i => i.FeatLen > 0).ToList();

        L($"雷柏接口 {interfaces.Count} 个，其中带 feature 报告 {targets.Count} 个");
        L();

        var handles = new Dictionary<string, (RawInterface itf, SafeFileHandle h, byte rid, string last)>();
        foreach (var itf in targets)
        {
            byte rid = (byte)itf.FeatureCaps.Select(c => c.ReportId).Distinct().OrderBy(x => x).FirstOrDefault();
            var h = HidNative.CreateFileW(itf.Path,
                HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { L($"[{itf.ShortPath}] 打不开 {Marshal.GetLastWin32Error()}"); continue; }
            handles[itf.ShortPath] = (itf, h, rid, "");
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        int polls = 0, changes = 0;

        while (DateTime.UtcNow < deadline)
        {
            polls++;
            foreach (var key in handles.Keys.ToList())
            {
                var (itf, h, rid, last) = handles[key];
                var buf = new byte[itf.FeatLen];
                buf[0] = rid;
                if (!HidNative.HidD_GetFeature(h, buf, buf.Length)) continue;
                string hex = HidProbe.Hex(buf, itf.FeatLen);
                if (hex != last)
                {
                    changes++;
                    L($"{DateTime.Now:HH:mm:ss.fff}  [{key}] RID=0x{rid:X2} 变化 → {hex}");
                    handles[key] = (itf, h, rid, hex);
                }
            }

            if (listenInput)
            {
                // 仅在明确启用时监听；会与官方驱动争抢报文，默认关闭
                Thread.Sleep(250);
            }
            else
            {
                Thread.Sleep(250);
            }
        }

        foreach (var v in handles.Values) v.h.Dispose();

        L();
        L($"轮询 {polls} 轮，捕获到 {changes} 次变化");
        if (changes == 0)
            L("全部 feature 报告在窗口期内没有变化 —— 说明官方驱动没走这条通道，或它一次性查完就不再刷新。");
        return sb.ToString();
    }
}
