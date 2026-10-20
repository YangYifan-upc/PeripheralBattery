using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery;

/// <summary>一个 HID 顶层集合（一个接口路径对应一个集合）。</summary>
internal sealed class HidCollectionInfo
{
    public string Path = "";
    public ushort Vid, Pid, Version;
    public string Product = "", Manufacturer = "";
    public string BusReportedName = "";
    public Guid? ContainerId;
    public ushort TopUsagePage, TopUsage;
    public ushort InLen, OutLen, FeatLen;
    public bool CanOpenRead;
    public uint OpenError;

    public bool IsVendor => TopUsagePage >= 0xFF00;

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

    public string ContainerKey => ContainerId?.ToString() ?? $"vid:{Vid:X4}:pid:{Pid:X4}";
}

internal static class Hid
{
    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
        IntPtr detail, uint detailSize, out uint requiredSize, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info,
        ref DEVPROPKEY key, out uint type, [Out] byte[] buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public ushort R0, R1, R2, R3, R4, R5, R6, R7, R8, R9, R10, R11, R12, R13, R14, R15, R16;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    private static readonly Guid ContainerIdFmtid = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private static readonly Guid BusReportedDescFmtid = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2");

    /// <summary>枚举本机全部 HID 顶层集合。只读，不写任何设备。</summary>
    public static List<HidCollectionInfo> Enumerate()
    {
        var result = new List<HidCollectionInfo>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did)) break;

                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                string? path = GetPath(set, ref did, ref info);
                if (string.IsNullOrEmpty(path)) continue;

                var c = Describe(path, set, ref info);
                if (c != null) result.Add(c);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string? GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, ref SP_DEVINFO_DATA info)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint required, ref info);
        if (required == 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out _, ref info)) return null;

            foreach (int offset in new[] { 4, 8 })
            {
                string? s = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset));
                if (!string.IsNullOrEmpty(s) && s[0] == '\\') return s;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidCollectionInfo? Describe(string path, IntPtr set, ref SP_DEVINFO_DATA info)
    {
        var c = new HidCollectionInfo { Path = path };

        // 只读打开即可拿到描述符；被系统独占的键鼠集合会失败，但描述符仍可通过 0 权限句柄读取
        var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            c.OpenError = (uint)Marshal.GetLastWin32Error();
            handle.Dispose();
            handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        else
        {
            c.CanOpenRead = true;
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (HidD_GetAttributes(handle, ref attr))
            {
                c.Vid = attr.VendorID;
                c.Pid = attr.ProductID;
                c.Version = attr.VersionNumber;
            }

            c.Product = ReadString(handle, true);
            c.Manufacturer = ReadString(handle, false);

            if (!HidD_GetPreparsedData(handle, out IntPtr pp) || pp == IntPtr.Zero) return null;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) != 0x00110000) return null;
                c.TopUsage = caps.Usage;
                c.TopUsagePage = caps.UsagePage;
                c.InLen = caps.InputReportByteLength;
                c.OutLen = caps.OutputReportByteLength;
                c.FeatLen = caps.FeatureReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(pp);
            }
        }
        finally
        {
            handle.Dispose();
        }

        c.ContainerId = ReadGuidProperty(set, ref info, ContainerIdFmtid, 2);
        c.BusReportedName = ReadStringProperty(set, ref info, BusReportedDescFmtid, 4);
        return c;
    }

    private static string ReadString(SafeFileHandle handle, bool product)
    {
        var buf = new byte[254];
        bool ok = product
            ? HidD_GetProductString(handle, buf, buf.Length)
            : HidD_GetManufacturerString(handle, buf, buf.Length);
        if (!ok) return "";
        string s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static Guid? ReadGuidProperty(IntPtr set, ref SP_DEVINFO_DATA info, Guid fmtid, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = fmtid, pid = pid };
        var buf = new byte[16];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, buf, 16, out _, 0)) return null;
        if (type != 13) return null;
        return new Guid(buf);
    }

    private static string ReadStringProperty(IntPtr set, ref SP_DEVINFO_DATA info, Guid fmtid, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = fmtid, pid = pid };
        var buf = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, buf, (uint)buf.Length, out uint req, 0))
            return "";
        if (type != 0x12) return ""; // DEVPROP_TYPE_STRING
        int len = (int)Math.Min(req, (uint)buf.Length);
        string s = Encoding.Unicode.GetString(buf, 0, len);
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Trim();
    }
}
