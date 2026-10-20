using System.Runtime.InteropServices;
using System.Text;

namespace Probe;

internal sealed class ValueCap
{
    public int ReportType;          // HidP_Input / HidP_Output / HidP_Feature
    public ushort ReportId;
    public ushort UsagePage;
    public ushort Usage;            // IsRange 时等于 UsageMin
    public ushort UsageMin;
    public ushort UsageMax;
    public bool IsRange;
    public ushort BitSize;
    public ushort ReportCount;
    public int LogicalMin;
    public int LogicalMax;
    public ushort LinkCollection;

    public bool ContainsUsage(ushort page, ushort usage)
        => UsagePage == page && (IsRange ? usage >= UsageMin && usage <= UsageMax : Usage == usage);

    public static string ReportTypeName(int t) => t switch
    {
        HidNative.HidP_Input => "Input",
        HidNative.HidP_Output => "Output",
        _ => "Feature"
    };
}

internal sealed class BatteryCandidate
{
    public ValueCap Cap = new();
    public bool ReadAttempted;
    public bool ReadOk;
    public string ReadDetail = "";
}

internal sealed class RawInterface
{
    public string Path = "";
    public ushort Vid, Pid, Version;
    public string Product = "", Manufacturer = "", Serial = "";
    public Guid? ContainerId;
    public ushort TopUsagePage, TopUsage;
    public ushort InLen, OutLen, FeatLen;
    public string Access = "打不开";
    public uint OpenError;
    public bool HasReadAccess;
    public readonly List<ValueCap> FeatureCaps = new();
    public readonly List<ValueCap> InputCaps = new();
    public readonly List<ValueCap> OutputCaps = new();
    public readonly List<string> CollectionTree = new();
    public readonly List<BatteryCandidate> BatteryCandidates = new();
    public string FeatureReadTest = "未测试";
    public List<string> Notes = new();

    public bool IsVendorPage => TopUsagePage >= 0xFF00;
    public bool IsRapoo => Vid == 0x24AE;

    public string ShortPath
    {
        get
        {
            int i = Path.IndexOf("hid#", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return Path;
            int j = Path.IndexOf('#', i + 4);
            return j < 0 ? Path[i..] : Path[i..j];
        }
    }
}

internal static class HidProbe
{
    public const ushort PageGenericDesktop = 0x01;
    public const ushort PageGenericDeviceControls = 0x06;
    public const ushort PageBatterySystem = 0x85;
    public const ushort PagePowerDevice = 0x84;
    public const ushort UsageBatteryStrength = 0x20;

    public static List<RawInterface> Enumerate()
    {
        var result = new List<RawInterface>();
        HidNative.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = HidNative.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            Console.Error.WriteLine($"SetupDiGetClassDevs 失败，错误码 {Marshal.GetLastWin32Error()}");
            return result;
        }

        try
        {
            var did = new HidNative.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<HidNative.SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0; ; index++)
            {
                if (!HidNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 259) // ERROR_NO_MORE_ITEMS
                        Console.Error.WriteLine($"枚举第 {index} 项失败，错误码 {err}");
                    break;
                }

                var info = new HidNative.SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<HidNative.SP_DEVINFO_DATA>()
                };

                string? path = GetInterfacePath(set, ref did, ref info, out _);
                if (string.IsNullOrEmpty(path)) continue;

                var itf = DescribeInterface(path, set, ref info);
                result.Add(itf);
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string? GetInterfacePath(IntPtr set, ref HidNative.SP_DEVICE_INTERFACE_DATA did,
        ref HidNative.SP_DEVINFO_DATA devInfo, out uint requiredSize)
    {
        requiredSize = 0;
        HidNative.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out requiredSize, ref devInfo);
        if (requiredSize == 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize = 8 (x64) / 6 (x86)
            int cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buffer, cbSize);

            if (!HidNative.SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, requiredSize, out _,
                    ref devInfo))
            {
                return null;
            }

            // DevicePath 在结构体里的偏移：4（默认对齐）。若首字符不是反斜杠，退到 8 再试一次。
            foreach (int offset in new[] { 4, 8 })
            {
                string? candidate = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset));
                if (!string.IsNullOrEmpty(candidate) && candidate[0] == '\\')
                    return candidate;
            }
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RawInterface DescribeInterface(string path, IntPtr set, ref HidNative.SP_DEVINFO_DATA devInfo)
    {
        var itf = new RawInterface { Path = path };

        // 先尝试读写打开；失败则用 0 权限打开（足以读描述符）
        var handle = HidNative.CreateFileW(path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);

        bool rw = !handle.IsInvalid;
        if (!rw)
        {
            itf.OpenError = (uint)Marshal.GetLastWin32Error();
            handle.Dispose();
            handle = HidNative.CreateFileW(path, 0,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            itf.Access = "仅描述符（0 权限）";
        }
        else
        {
            itf.Access = "读写";
            itf.HasReadAccess = true;
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            itf.Access = $"打不开 (错误码 {itf.OpenError})";
            itf.ContainerId = ReadContainerId(set, ref devInfo);
            return itf;
        }

        try
        {
            var attr = new HidNative.HIDD_ATTRIBUTES
            {
                Size = (uint)Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>()
            };
            if (HidNative.HidD_GetAttributes(handle, ref attr))
            {
                itf.Vid = attr.VendorID;
                itf.Pid = attr.ProductID;
                itf.Version = attr.VersionNumber;
            }
            else
            {
                itf.Notes.Add($"HidD_GetAttributes 失败 (错误码 {Marshal.GetLastWin32Error()})");
            }

            itf.Product = ReadString(handle, StringKind.Product);
            itf.Manufacturer = ReadString(handle, StringKind.Manufacturer);
            itf.Serial = ReadString(handle, StringKind.Serial);

            if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero)
            {
                itf.Notes.Add($"HidD_GetPreparsedData 失败 (错误码 {Marshal.GetLastWin32Error()})");
                itf.ContainerId = ReadContainerId(set, ref devInfo);
                return itf;
            }

            try
            {
                int st = HidNative.HidP_GetCaps(pp, out HidNative.HIDP_CAPS caps);
                if (st != HidNative.HIDP_STATUS_SUCCESS)
                {
                    itf.Notes.Add($"HidP_GetCaps 失败 (NTSTATUS 0x{st:X8})");
                    return itf;
                }

                itf.TopUsagePage = caps.UsagePage;
                itf.TopUsage = caps.Usage;
                itf.InLen = caps.InputReportByteLength;
                itf.OutLen = caps.OutputReportByteLength;
                itf.FeatLen = caps.FeatureReportByteLength;

                CollectCaps(pp, HidNative.HidP_Feature, itf.FeatureCaps);
                CollectCaps(pp, HidNative.HidP_Input, itf.InputCaps);
                CollectCaps(pp, HidNative.HidP_Output, itf.OutputCaps);
                CollectCollectionTree(pp, caps.NumberLinkCollectionNodes, itf.CollectionTree);

                FindBatteryCandidates(itf);

                // 只读验证：对有 Feature 报告长度且能读的接口，实测一次 HidD_GetFeature
                if (itf.FeatLen > 0)
                    itf.FeatureReadTest = TryReadFeature(handle, itf, pp);

                // 对找到的电量候选字段做一次真实读取（只读）
                foreach (var cand in itf.BatteryCandidates)
                    TryReadCandidate(handle, itf, pp, cand);
            }
            finally
            {
                HidNative.HidD_FreePreparsedData(pp);
            }
        }
        finally
        {
            itf.ContainerId = ReadContainerId(set, ref devInfo);
            handle.Dispose();
        }

        return itf;
    }

    private enum StringKind { Product, Manufacturer, Serial }

    private static string ReadString(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringKind kind)
    {
        var buf = new byte[254];
        bool ok = kind switch
        {
            StringKind.Product => HidNative.HidD_GetProductString(handle, buf, buf.Length),
            StringKind.Manufacturer => HidNative.HidD_GetManufacturerString(handle, buf, buf.Length),
            _ => HidNative.HidD_GetSerialNumberString(handle, buf, buf.Length)
        };
        if (!ok) return "";
        string s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static void CollectCaps(IntPtr pp, int reportType, List<ValueCap> sink)
    {
        ushort len = 0;
        int st = HidNative.HidP_GetValueCaps(reportType, IntPtr.Zero, ref len, pp);
        if (len == 0) return;
        if (st != HidNative.HIDP_STATUS_SUCCESS && st != HidNative.HIDP_STATUS_BUFFER_TOO_SMALL)
        {
            Console.Error.WriteLine($"HidP_GetValueCaps(type={reportType}) 探测长度失败 NTSTATUS 0x{st:X8}");
            return;
        }

        int elementSize = Math.Max(Marshal.SizeOf<HidNative.HIDP_VALUE_CAPS>(), 72);
        int capacity = len + 8;
        IntPtr buffer = Marshal.AllocHGlobal(elementSize * capacity);
        try
        {
            ushort n = (ushort)capacity;
            st = HidNative.HidP_GetValueCaps(reportType, buffer, ref n, pp);
            if (st != HidNative.HIDP_STATUS_SUCCESS)
            {
                Console.Error.WriteLine($"HidP_GetValueCaps(type={reportType}) 读取失败 NTSTATUS 0x{st:X8}");
                return;
            }

            for (int i = 0; i < n; i++)
            {
                var vc = Marshal.PtrToStructure<HidNative.HIDP_VALUE_CAPS>(IntPtr.Add(buffer, i * elementSize));
                sink.Add(new ValueCap
                {
                    ReportType = reportType,
                    ReportId = vc.ReportID,
                    UsagePage = vc.UsagePage,
                    Usage = vc.UsageMin,
                    UsageMin = vc.UsageMin,
                    UsageMax = vc.UsageMax,
                    IsRange = vc.IsRange != 0,
                    BitSize = vc.BitSize,
                    ReportCount = vc.ReportCount,
                    LogicalMin = vc.LogicalMin,
                    LogicalMax = vc.LogicalMax,
                    LinkCollection = vc.LinkCollection
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectCollectionTree(IntPtr pp, ushort count, List<string> sink)
    {
        if (count == 0) return;
        int elementSize = Math.Max(Marshal.SizeOf<HidNative.HIDP_LINK_COLLECTION_NODE>(), IntPtr.Size == 8 ? 24 : 20);
        int capacity = count + 8;
        IntPtr buffer = Marshal.AllocHGlobal(elementSize * capacity);
        try
        {
            uint n = (uint)capacity;
            int st = HidNative.HidP_GetLinkCollectionNodes(buffer, ref n, pp);
            if (st != HidNative.HIDP_STATUS_SUCCESS) return;

            var nodes = new List<HidNative.HIDP_LINK_COLLECTION_NODE>();
            for (int i = 0; i < n; i++)
                nodes.Add(Marshal.PtrToStructure<HidNative.HIDP_LINK_COLLECTION_NODE>(IntPtr.Add(buffer, i * elementSize)));

            void Walk(int index, int depth)
            {
                if (index < 0 || index >= nodes.Count || depth > 6) return;
                var node = nodes[index];
                sink.Add($"{new string(' ', depth * 2)}COL[{index}] {node.LinkUsagePage:X2}:{node.LinkUsage:X4} " +
                         $"children={node.NumberOfChildren}");
                int child = node.FirstChild;
                int guard = 0;
                while (child != 0 && child < nodes.Count && guard++ < 64)
                {
                    Walk(child, depth + 1);
                    child = nodes[child].NextSibling;
                }
            }

            if (nodes.Count > 0) Walk(0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void FindBatteryCandidates(RawInterface itf)
    {
        foreach (var list in new[] { itf.FeatureCaps, itf.InputCaps, itf.OutputCaps })
        {
            foreach (var cap in list)
            {
                if (cap.ContainsUsage(PageGenericDeviceControls, UsageBatteryStrength))
                    itf.BatteryCandidates.Add(new BatteryCandidate { Cap = cap });
            }
        }

        // 附加线索：Battery System(0x85) / Power Device(0x84) 页，只记录不解释
        foreach (var list in new[] { itf.FeatureCaps, itf.InputCaps })
        {
            foreach (var cap in list)
            {
                if (cap.UsagePage is PageBatterySystem or PagePowerDevice)
                    itf.Notes.Add($"发现 {(cap.UsagePage == PageBatterySystem ? "Battery System(0x85)" : "Power Device(0x84)")} 字段 " +
                                  $"{ValueCap.ReportTypeName(cap.ReportType)} {cap.UsagePage:X2}:{cap.Usage:X4} RID=0x{cap.ReportId:X2}");
            }
        }
    }

    private static string TryReadFeature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, RawInterface itf, IntPtr pp)
    {
        if (!itf.HasReadAccess) return $"跳过（句柄无读权限，位置={itf.Access}）";

        var ids = itf.FeatureCaps.Select(c => c.ReportId).Distinct().OrderBy(x => x).ToList();
        if (ids.Count == 0) ids.Add(0);

        var parts = new List<string>();
        foreach (byte rid in ids)
        {
            var buf = new byte[itf.FeatLen];
            buf[0] = rid;
            bool ok = HidNative.HidD_GetFeature(handle, buf, buf.Length);
            parts.Add(ok
                ? $"RID0x{rid:X2}=成功[{Hex(buf, 33)}]"
                : $"RID0x{rid:X2}=失败({Marshal.GetLastWin32Error()})");
        }
        return string.Join("  ", parts);
    }

    private static void TryReadCandidate(Microsoft.Win32.SafeHandles.SafeFileHandle handle, RawInterface itf,
        IntPtr pp, BatteryCandidate cand)
    {
        cand.ReadAttempted = true;
        if (!itf.HasReadAccess)
        {
            cand.ReadDetail = "句柄无读权限，未尝试";
            return;
        }

        var cap = cand.Cap;
        if (cap.ReportType == HidNative.HidP_Output)
        {
            cand.ReadDetail = "Output 报告不可读";
            return;
        }

        int reportLen = cap.ReportType == HidNative.HidP_Feature ? itf.FeatLen : itf.InLen;
        if (reportLen <= 0)
        {
            cand.ReadDetail = "报告长度为 0";
            return;
        }

        byte[] buf = new byte[reportLen];
        if (cap.ReportId != 0) buf[0] = (byte)cap.ReportId;

        bool ok;
        if (cap.ReportType == HidNative.HidP_Feature)
        {
            ok = HidNative.HidD_GetFeature(handle, buf, buf.Length);
        }
        else
        {
            // Input 报告需要 ReadFile 读取。本探测阶段为避免与系统抢输入流，不实现。
            cand.ReadDetail = "Input 报告需持续读取，探测阶段不抢流（第二阶段处理）";
            return;
        }

        if (!ok)
        {
            cand.ReadDetail = $"HidD_GetFeature 失败 错误码 {Marshal.GetLastWin32Error()}";
            return;
        }

        cand.ReadOk = true;
        int st = HidNative.HidP_GetScaledUsageValue(cap.ReportType, PageGenericDeviceControls,
            cap.LinkCollection, UsageBatteryStrength, out int value, pp, buf, (uint)buf.Length);
        cand.ReadDetail = st == HidNative.HIDP_STATUS_SUCCESS
            ? $"读到原始值 {value}（Logical {cap.LogicalMin}..{cap.LogicalMax}）| {Hex(buf, 24)}"
            : $"报告读到但取值失败 NTSTATUS 0x{st:X8} | {Hex(buf, 24)}";
    }

    private static Guid? ReadContainerId(IntPtr set, ref HidNative.SP_DEVINFO_DATA devInfo)
    {
        var key = new HidNative.DEVPROPKEY
        {
            fmtid = HidNative.DevPkeyContainerIdFmtid,
            pid = 2
        };
        var buffer = new byte[16];
        if (!HidNative.SetupDiGetDevicePropertyW(set, ref devInfo, ref key, out uint type, buffer,
                (uint)buffer.Length, out _, 0))
            return null;
        if (type != 13) return null; // DEVPROP_TYPE_GUID
        return new Guid(buffer);
    }

    public static string Hex(byte[] data, int max)
    {
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        if (data.Length > max) sb.Append("…");
        return sb.ToString().TrimEnd();
    }
}
