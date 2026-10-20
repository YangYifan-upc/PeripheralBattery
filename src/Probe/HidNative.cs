using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Probe;

/// <summary>
/// 本机 HID 相关底层调用。
/// 硬约束：本文件故意不声明 HidD_SetFeature / HidD_SetOutputReport / WriteFile，
/// 物理上不存在写入通道，杜绝探测阶段误发命令的可能性。
/// </summary>
internal static class HidNative
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public static readonly int HIDP_STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0110007);
    public static readonly int HIDP_STATUS_USAGE_NOT_FOUND = unchecked((int)0xC0110004);
    public static readonly int HIDP_STATUS_INVALID_REPORT_LENGTH = unchecked((int)0xC0110003);
    public static readonly int HIDP_STATUS_INCOMPATIBLE_REPORT_ID = unchecked((int)0xC0110005);

    public const int HidP_Input = 0;
    public const int HidP_Output = 1;
    public const int HidP_Feature = 2;

    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    // ---------------- hid.dll ----------------

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    // BufferLength 单位是「字节」，不是字符数
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, [Out] byte[] buffer, int bufferLength);

    /// <summary>只读：读取设备的 feature 报告。本文件不提供任何写入变体。</summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle h, [Out] byte[] reportBuffer, int reportBufferLength);

    // 以下四个返回 NTSTATUS（int）。不设 SetLastError，不要读 GetLastWin32Error。
    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetLinkCollectionNodes(IntPtr nodes, ref uint nodeLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetScaledUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out int usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    // ---------------- setupapi.dll ----------------

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref DEVPROPKEY propertyKey, out uint propertyType, [Out] byte[] propertyBuffer,
        uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---------------- kernel32.dll ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // ---------------- 结构体 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public ushort Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7,
                     Reserved8, Reserved9, Reserved10, Reserved11, Reserved12, Reserved13, Reserved14, Reserved15, Reserved16;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    /// <summary>对应 hidpi.h 的 HIDP_VALUE_CAPS。x64 下 sizeof 应为 72。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2_0, Reserved2_1, Reserved2_2, Reserved2_3, Reserved2_4;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // union：Range / NotRange 各 8 个 USHORT，取并集后前 6 个足够区分
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_LINK_COLLECTION_NODE
    {
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public ushort Parent;
        public ushort NumberOfChildren;
        public ushort NextSibling;
        public ushort FirstChild;
        public uint CollectionTypeBits;
        public IntPtr UserContext;
    }

    public static readonly Guid DevPkeyContainerIdFmtid = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");

    public static ushort UsageOf(in HIDP_VALUE_CAPS cap)
    {
        // IsRange != 0 时用法在 UsageMin..UsageMax 区间内；否则单值在 UsageMin（即 NotRange.Usage）
        return cap.UsageMin;
    }
}
