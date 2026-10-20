using System.Text;

namespace Probe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向时可能失败，忽略 */ }

        if (args.Any(a => a.Equals("--decode", StringComparison.OrdinalIgnoreCase)))
        {
            int seconds = 20;
            var secArg = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
            if (secArg != null && int.TryParse(secArg["--seconds=".Length..], out int s3) && s3 > 0 && s3 <= 600)
                seconds = s3;

            string report = DecodeProbe.Run(seconds);
            string decodePath = Path.Combine(AppContext.BaseDirectory, "decode-log.txt");
            try { File.WriteAllText(decodePath, report, new UTF8Encoding(false)); } catch { }
            Console.WriteLine(report);
            Console.WriteLine($"日志已写入: {decodePath}");
            return 0;
        }

        if (args.Any(a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
        {
            int seconds = 120;
            var secArg = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
            if (secArg != null && int.TryParse(secArg["--seconds=".Length..], out int s2) && s2 > 0 && s2 <= 600)
                seconds = s2;

            string report = WatchProbe.Run(seconds, args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase)));
            string watchPath = Path.Combine(AppContext.BaseDirectory, "watch-log.txt");
            try { File.WriteAllText(watchPath, report, new UTF8Encoding(false)); } catch { }
            Console.WriteLine(report);
            Console.WriteLine($"日志已写入: {watchPath}");
            return 0;
        }

        if (args.Any(a => a.Equals("--vendor", StringComparison.OrdinalIgnoreCase)))
        {
            int seconds = 45;
            var secArg = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
            if (secArg != null && int.TryParse(secArg["--seconds=".Length..], out int s) && s > 0 && s <= 300)
                seconds = s;

            string vendorReport = VendorProbe.Run(seconds);
            string outPath = Path.Combine(AppContext.BaseDirectory, "vendor-probe.txt");
            try { File.WriteAllText(outPath, vendorReport, new UTF8Encoding(false)); } catch { }
            Console.WriteLine(vendorReport);
            Console.WriteLine($"报告已写入: {outPath}");
            return 0;
        }

        var sb = new StringBuilder();
        void Line(string s = "") { sb.AppendLine(s); }

        Line("================================================================");
        Line($"PeripheralBattery Probe  v0.1.0   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"OS {Environment.OSVersion.Version} {(Environment.Is64BitProcess ? "x64" : "x86")} | " +
             $"Runtime {Environment.Version} | Elevated: {IsElevated()}");
        Line($"结构体自校验: HIDP_CAPS={System.Runtime.InteropServices.Marshal.SizeOf<HidNative.HIDP_CAPS>()} " +
             $"HIDP_VALUE_CAPS={System.Runtime.InteropServices.Marshal.SizeOf<HidNative.HIDP_VALUE_CAPS>()} " +
             $"HIDD_ATTRIBUTES={System.Runtime.InteropServices.Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>()}");
        Line("================================================================");
        Line();

        List<RawInterface> list;
        try
        {
            list = HidProbe.Enumerate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Line("枚举异常：" + ex);
            WriteReport(sb);
            return 1;
        }

        int opened = list.Count(x => x.HasReadAccess);
        int descOnly = list.Count(x => x.Access.StartsWith("仅描述符"));
        int failed = list.Count(x => x.Access.StartsWith("打不开"));

        Line($"HID 接口总数: {list.Count}   可读写: {opened}   仅描述符: {descOnly}   打不开: {failed}");
        Line();

        int index = 1;
        foreach (var itf in list)
        {
            Line($"[{index:00}] {itf.ShortPath}");
            Line($"  ContainerId : {(itf.ContainerId?.ToString() ?? "(未知)")}");
            Line($"  VID/PID/Ver : {itf.Vid:X4} / {itf.Pid:X4} / {itf.Version:X4}" +
                 (itf.IsRapoo ? "   (Rapoo)" : ""));
            Line($"  Product     : \"{itf.Product}\"");
            Line($"  Manufacturer: \"{itf.Manufacturer}\"   Serial: \"{itf.Serial}\"");
            Line($"  TopLevel    : UsagePage=0x{itf.TopUsagePage:X2} ({PageName(itf.TopUsagePage)})  " +
                 $"Usage=0x{itf.TopUsage:X2} ({UsageName(itf.TopUsagePage, itf.TopUsage)})");
            Line($"  Access      : {itf.Access}" + (itf.OpenError != 0 ? $"  (首次错误码 {itf.OpenError})" : ""));
            Line($"  ReportLen   : In={itf.InLen}  Out={itf.OutLen}  Feat={itf.FeatLen}");
            Line($"  GetFeature  : {itf.FeatureReadTest}");

            foreach (var note in itf.Notes) Line($"  ! {note}");

            AppendCaps(Line, "Feature", itf.FeatureCaps);
            AppendCaps(Line, "Input", itf.InputCaps);
            if (itf.OutputCaps.Count > 0) AppendCaps(Line, "Output", itf.OutputCaps);

            if (itf.CollectionTree.Count > 0)
            {
                Line("  LinkCollections:");
                foreach (var node in itf.CollectionTree.Take(12)) Line("    " + node);
            }

            if (itf.BatteryCandidates.Count == 0)
            {
                Line("  Battery     : 无标准电量字段(0x06:0x20)");
            }
            else
            {
                foreach (var c in itf.BatteryCandidates)
                {
                    Line($"  Battery     : 命中 {ValueCap.ReportTypeName(c.Cap.ReportType)} RID=0x{c.Cap.ReportId:X2} " +
                         $"BitSize={c.Cap.BitSize} Logical={c.Cap.LogicalMin}..{c.Cap.LogicalMax} " +
                         $"LinkCollection={c.Cap.LinkCollection}");
                    Line($"                实测: {c.ReadDetail}");
                }
            }

            Line();
            index++;
        }

        AppendGroups(Line, list);
        AppendWinRt(Line, await WinRtProbe.RunAsync());
        int exit = AppendVerdict(Line, list);

        string reportPath = Path.Combine(AppContext.BaseDirectory, "probe-report.txt");
        WriteReport(sb, reportPath);

        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告已写入: {reportPath}");
        Console.WriteLine($"退出码: {exit}");
        return exit;
    }

    private static void WriteReport(StringBuilder sb, string? path = null)
    {
        try
        {
            path ??= Path.Combine(AppContext.BaseDirectory, "probe-report.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写报告失败: " + ex.Message);
        }
    }

    private static void AppendCaps(Action<string> line, string title, List<ValueCap> caps)
    {
        if (caps.Count == 0) return;
        line($"  ValueCaps({title}): 共 {caps.Count} 项");
        line("    Page Usage  RID  Bits Cnt  LogicalMin..Max  LinkCol");
        foreach (var c in caps.Take(40))
        {
            string usage = c.IsRange ? $"{c.UsageMin:X4}-{c.UsageMax:X4}" : $"{c.Usage:X4}";
            line($"    0x{c.UsagePage:X2} {usage,-9} 0x{c.ReportId:X2} {c.BitSize,4} {c.ReportCount,3}  " +
                 $"{c.LogicalMin,6}..{c.LogicalMax,-6}  {c.LinkCollection}");
        }
        if (caps.Count > 40) line($"    …(另有 {caps.Count - 40} 项省略)");
    }

    private static void AppendGroups(Action<string> line, List<RawInterface> list)
    {
        line("----------------------------------------------------------------");
        line("按物理设备归并（ContainerId）");
        var groups = list.GroupBy(x => x.ContainerId?.ToString() ?? $"无容器:{x.Vid:X4}:{x.Pid:X4}").ToList();
        foreach (var g in groups)
        {
            var first = g.First();
            var usages = g.Select(x => $"0x{x.TopUsagePage:X2}:0x{x.TopUsage:X2}").Distinct().ToList();
            line($"  设备 {first.Vid:X4}:{first.Pid:X4} \"{first.Product}\"  接口 {g.Count()} 个  " +
                 $"顶层用途 {string.Join(", ", usages)}");
        }
        line(string.Empty);
    }

    private static void AppendWinRt(Action<string> line, WinRtResult? result)
    {
        line("----------------------------------------------------------------");
        line("Windows 电量接口 (Windows.Devices.Power.Battery)");
        if (result == null)
        {
            line("  未启用（本次构建未包含 WinRT 支持）");
            line(string.Empty);
            return;
        }
        if (result.Error != null)
        {
            line("  调用失败: " + result.Error);
            line(string.Empty);
            return;
        }
        line($"  Selector: {result.Selector}");
        line($"  命中数: {result.Items.Count}");
        foreach (var item in result.Items)
        {
            line($"  - {item.Name}");
            line($"    Id     = {item.Id}");
            line($"    Status = {item.Status}   Remaining={item.Remaining} mWh   Full={item.Full} mWh");
        }
        line(string.Empty);
    }

    private static int AppendVerdict(Action<string> line, List<RawInterface> list)
    {
        line("----------------------------------------------------------------");
        line("判定");

        var readable = list.SelectMany(x => x.BatteryCandidates).Where(c => c.ReadOk).ToList();
        var anyStandard = list.Any(x => x.BatteryCandidates.Count > 0);
        var vendorOnly = list.Where(x => x.IsVendorPage).ToList();

        line($"  标准 0x06:0x20 字段: {(anyStandard ? "有" : "无")}" +
             (anyStandard ? $"（集合 {list.Count(x => x.BatteryCandidates.Count > 0)} 个，实测读到值 {readable.Count} 个）" : ""));
        line($"  厂商私有集合: {vendorOnly.Count} 个" +
             (vendorOnly.Count > 0 ? "（" + string.Join(", ", vendorOnly.Select(v => $"0x{v.TopUsagePage:X2}:0x{v.TopUsage:X2}")) + "）" : ""));

        int code;
        string conclusion;
        if (readable.Count > 0)
        {
            code = 0;
            conclusion = "STANDARD_BATTERY_READABLE —— 有标准电量通道且实测读到了值，可直接进入 M1/M2";
        }
        else if (anyStandard)
        {
            code = 4;
            conclusion = "STANDARD_BATTERY_BLOCKED —— 设备声明了标准电量字段，但当前权限下读不出来，" +
                         "需要改走同一接收器的厂商集合或 Windows 电量接口";
        }
        else if (vendorOnly.Count > 0)
        {
            code = 2;
            conclusion = "VENDOR_ONLY —— 只有厂商私有集合，没有标准电量字段，需要 M5 抓包逆向";
        }
        else
        {
            code = 3;
            conclusion = "NO_BATTERY_CHANNEL —— 未发现任何电量通道";
        }

        line($"  结论: {conclusion}");
        line($"  退出码: {code}");
        return code;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string PageName(ushort page) => page switch
    {
        0x01 => "Generic Desktop",
        0x06 => "Generic Device Controls",
        0x07 => "Keyboard",
        0x08 => "LED",
        0x09 => "Button",
        0x0C => "Consumer",
        0x0D => "Digitizer",
        0x84 => "Power Device",
        0x85 => "Battery System",
        >= 0xFF00 => "Vendor",
        _ => "?"
    };

    public static string UsageName(ushort page, ushort usage) => page switch
    {
        0x01 => usage switch
        {
            0x01 => "Pointer",
            0x02 => "Mouse",
            0x04 => "Joystick",
            0x05 => "Gamepad",
            0x06 => "Keyboard",
            0x07 => "Keypad",
            0x80 => "System Control",
            _ => "?"
        },
        0x06 => usage == 0x20 ? "Battery Strength" : "?",
        0x0C => usage == 0x01 ? "Consumer Control" : "?",
        _ => "?"
    };
}

internal sealed class WinRtItem
{
    public string Id = "", Name = "", Status = "";
    public int? Remaining, Full;
}

internal sealed class WinRtResult
{
    public string Selector = "";
    public List<WinRtItem> Items = new();
    public string? Error;
}

internal static class WinRtProbe
{
    public static async Task<WinRtResult?> RunAsync()
    {
#if USE_WINRT
        var result = new WinRtResult();
        try
        {
            result.Selector = Windows.Devices.Power.Battery.GetDeviceSelector();
            var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(result.Selector);
            foreach (var info in infos)
            {
                var item = new WinRtItem { Id = info.Id, Name = info.Name };
                try
                {
                    var battery = await Windows.Devices.Power.Battery.FromIdAsync(info.Id);
                    var report = battery?.GetReport();
                    if (report != null)
                    {
                        item.Status = report.Status.ToString();
                        item.Remaining = report.RemainingCapacityInMilliwattHours;
                        item.Full = report.FullChargeCapacityInMilliwattHours;
                    }
                    else
                    {
                        item.Status = "(GetReport 返回空)";
                    }
                }
                catch (Exception ex)
                {
                    item.Status = "异常: " + ex.Message;
                }
                result.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }
        return result;
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}
