using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HardwareScope.Core;

internal sealed class NativeTelemetry
{
    internal sealed record DiskTemperatures(List<(string Name, double Celsius)> Readings, string? UnavailableReason);
    private ulong _idle;
    private ulong _kernel;
    private ulong _user;
    private bool _cpuPrimed;

    public double? ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user;
        var previous = _kernel + _user;
        double? result = _cpuPrimed && total > previous
            ? Math.Clamp(100.0 * (total - previous - Math.Min(idle - _idle, total - previous)) / (total - previous), 0, 100)
            : null;
        _idle = idle; _kernel = kernel; _user = user; _cpuPrimed = true;
        return result;
    }

    public static (ulong Total, ulong Available, uint Load)? ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhysical, status.AvailablePhysical, status.MemoryLoad) : null;
    }

    public static double? ReadCpuFrequencyMHz()
    {
        var information = new ProcessorPowerInformation[Environment.ProcessorCount];
        if (CallNtPowerInformation(11, IntPtr.Zero, 0, information, (uint)(Marshal.SizeOf<ProcessorPowerInformation>() * information.Length)) != 0)
            return null;
        var frequencies = information.Where(p => p.CurrentMHz > 0).Select(p => (double)p.CurrentMHz).ToArray();
        return frequencies.Length > 0 ? frequencies.Average() : null;
    }

    public static int ProbePawnIoAccess()
    {
        // Match LibreHardwareMonitor's required access without installing a driver or loading a module.
        using var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    public static DiskTemperatures ReadNvmeTemperatures(string devicePath)
    {
        // STORAGE_QUERY_PROPERTY is read-only and needs no disk write access or elevated token.
        using var handle = CreateFileW(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return new([], StorageError(Marshal.GetLastWin32Error()));
        var buffer = new byte[8 + 40 + 512];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), 50); // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 3); // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 2); // NVMeDataTypeLogPage
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 2); // NVME_LOG_PAGE_HEALTH_INFO
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), 512);
        if (!DeviceIoControl(handle, 0x2D1400, buffer, (uint)buffer.Length, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
            return new([], StorageError(Marshal.GetLastWin32Error()));
        if (returned < 48 || returned > buffer.Length || BinaryPrimitives.ReadUInt32LittleEndian(buffer) < 48 ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) < 48 ||
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)) != 3 || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)) != 2)
            return new([], "存储驱动返回的 NVMe SMART 数据头无效，未采用该读数。");
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(28));
        if (offset < 40 || length < 512 || offset > returned - 8 || length > returned - 8 - offset)
            return new([], "存储驱动返回的 NVMe SMART 数据长度无效，未采用该读数。");
        var log = buffer.AsSpan((int)offset + 8, (int)length);
        var readings = new List<(string Name, double Celsius)>();
        var composite = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        if (composite != 0) readings.Add(("NVMe 综合温度", Math.Round(composite - 273.15, 2)));
        for (var index = 0; index < 8; index++)
        {
            var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[(200 + index * 2)..]);
            if (kelvin != 0) readings.Add(($"NVMe 温度传感器 #{index + 1}", Math.Round(kelvin - 273.15, 2)));
        }
        return new(readings, readings.Count > 0 ? null : "NVMe SMART 日志未提供有效温度；未实现的零值不作温度显示。");
    }

    private static string StorageError(int error) => error switch
    {
        5 => "Windows 拒绝读取该物理磁盘的 SMART 温度（错误 5，访问被拒绝）；存储驱动或设备权限限制此只读查询。",
        1 or 50 or 87 => $"该存储驱动不支持 NVMe SMART 只读温度查询（Windows 错误 {error}）；SATA、USB 桥接或驱动未转发此协议也会出现此结果。",
        2 or 3 or 21 or 1167 => $"物理磁盘暂时不可用或已移除（Windows 错误 {error}）。",
        _ => $"Windows 未能读取 NVMe SMART 温度（错误 {error}）。"
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number, MaxMHz, CurrentMHz, MHzLimit, MaxIdleState, CurrentIdleState;
    }
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(uint informationLevel, IntPtr input, uint inputLength,
        [Out] ProcessorPowerInformation[] output, uint outputLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
