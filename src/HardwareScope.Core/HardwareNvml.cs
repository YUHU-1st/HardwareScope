using System.Runtime.InteropServices;
using System.Text;

namespace HardwareScope.Core;

internal sealed record HardwareNvmlSnapshot(string Id, string Name, List<SensorReading> Sensors,
    List<SensorReading> MemorySensors, OverclockInfo Overclock, OverclockInfo MemoryOverclock);

internal sealed class HardwareNvml : IDisposable
{
    private IntPtr _library;
    private readonly List<(uint Index, IntPtr Handle, string Name)> _devices = [];
    private NoArgs? _shutdown;
    private GetUnsigned? _power, _powerLimit, _fan;
    private GetTypedUnsigned? _temperature, _clock;
    private GetUtilization? _utilization;
    private GetMemory? _memory;
    private GetMask? _reasons;
    private GetOffset? _coreOffset, _memoryOffset;
    private GetClockOffsets? _clockOffsets;
    public string? UnavailableReason { get; private set; }

    public HardwareNvml()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")
        };
        foreach (var path in candidates)
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _library)) break;
        if (_library == IntPtr.Zero) { UnavailableReason = "未找到 NVIDIA 驱动的 NVML 库。"; return; }
        var init = Export<NoArgs>("nvmlInit_v2");
        if (init is null || init() != 0) { UnavailableReason = "NVIDIA NVML 初始化失败。"; return; }
        _shutdown = Export<NoArgs>("nvmlShutdown");
        var count = Export<GetCount>("nvmlDeviceGetCount_v2");
        var handle = Export<GetHandle>("nvmlDeviceGetHandleByIndex_v2");
        var name = Export<GetName>("nvmlDeviceGetName");
        _power = Export<GetUnsigned>("nvmlDeviceGetPowerUsage");
        _powerLimit = Export<GetUnsigned>("nvmlDeviceGetPowerManagementLimit");
        _fan = Export<GetUnsigned>("nvmlDeviceGetFanSpeed");
        _temperature = Export<GetTypedUnsigned>("nvmlDeviceGetTemperature");
        _clock = Export<GetTypedUnsigned>("nvmlDeviceGetClockInfo");
        _utilization = Export<GetUtilization>("nvmlDeviceGetUtilizationRates");
        _memory = Export<GetMemory>("nvmlDeviceGetMemoryInfo");
        _reasons = Export<GetMask>("nvmlDeviceGetCurrentClocksEventReasons") ?? Export<GetMask>("nvmlDeviceGetCurrentClocksThrottleReasons");
        _coreOffset = Export<GetOffset>("nvmlDeviceGetGpcClkVfOffset");
        _memoryOffset = Export<GetOffset>("nvmlDeviceGetMemClkVfOffset");
        _clockOffsets = Export<GetClockOffsets>("nvmlDeviceGetClockOffsets");
        if (count is null || handle is null || name is null || count(out var number) != 0) return;
        for (uint i = 0; i < number; i++)
        {
            if (handle(i, out var device) != 0) continue;
            var buffer = new StringBuilder(256);
            if (name(device, buffer, 256) == 0) _devices.Add((i, device, buffer.ToString()));
        }
    }

    public List<HardwareNvmlSnapshot> Sample()
    {
        var result = new List<HardwareNvmlSnapshot>();
        foreach (var device in _devices)
        {
            var id = $"nvml/gpu{device.Index}";
            var sensors = new List<SensorReading>();
            var memorySensors = new List<SensorReading>();
            uint u = 0;
            Utilization utilizationValue = default;
            var status = _utilization is null ? 3 : _utilization(device.Handle, out utilizationValue);
            sensors.Add(Reading(id, "load", "GPU 占用率", "Load", status == 0 ? utilizationValue.Gpu : null, "%", status));
            sensors.Add(Reading(id, "memory-controller-load", "显存控制器占用率", "Load", status == 0 ? utilizationValue.Memory : null, "%", status));
            status = _clock is null ? 3 : _clock(device.Handle, 0, out u);
            sensors.Add(Reading(id, "clock", "GPU 核心频率", "Clock", status == 0 ? u : null, "MHz", status));
            u = 0; status = _clock is null ? 3 : _clock(device.Handle, 2, out u);
            memorySensors.Add(Reading(id + "/vram", "clock", "显存频率", "Clock", status == 0 ? u : null, "MHz", status));
            u = 0; status = _temperature is null ? 3 : _temperature(device.Handle, 0, out u);
            sensors.Add(Reading(id, "temperature", "GPU 温度", "Temperature", status == 0 ? u : null, "°C", status));
            u = 0; status = _power is null ? 3 : _power(device.Handle, out u);
            sensors.Add(Reading(id, "power", "GPU 功耗", "Power", status == 0 ? u / 1000.0 : null, "W", status));
            u = 0; status = _powerLimit is null ? 3 : _powerLimit(device.Handle, out u);
            sensors.Add(Reading(id, "power-cap", "当前功率上限", "Other", status == 0 ? u / 1000.0 : null, "W", status));
            u = 0; status = _fan is null ? 3 : _fan(device.Handle, out u);
            sensors.Add(Reading(id, "fan-duty", "GPU 风扇速度（额定百分比）", "Other", status == 0 ? u : null, "%", status));
            ulong mask = 0; status = _reasons is null ? 3 : _reasons(device.Handle, out mask);
            sensors.Add(Reading(id, "limit", status == 0 ? "性能限制：" + ReasonNames(mask) : "性能限制状态", "Limit",
                status == 0 ? ((mask & 0x1FEUL) != 0 ? 1 : 0) : null, "", status));
            sensors.Add(Reading(id, "idle", "因空闲降频", "Other", status == 0 ? ((mask & 1) != 0 ? 1 : 0) : null, "", status));
            DeviceMemory mem = default; status = _memory is null ? 3 : _memory(device.Handle, out mem);
            memorySensors.Add(Reading(id + "/vram", "used", "已用显存", "Data", status == 0 ? mem.Used / 1048576.0 : null, "MiB", status));
            memorySensors.Add(Reading(id + "/vram", "total", "显存容量", "Data", status == 0 ? mem.Total / 1048576.0 : null, "MiB", status));
            memorySensors.Add(Reading(id + "/vram", "load", "显存占用率", "Load", status == 0 && mem.Total > 0 ? 100.0 * mem.Used / mem.Total : null, "%", status));
            var core = ReadOffset(device.Handle, 0, _coreOffset);
            var memory = ReadOffset(device.Handle, 2, _memoryOffset);
            var state = core > 0 || memory > 0 ? "Confirmed" : core == 0 && memory == 0 ? "NoOffset" : "Unknown";
            var evidence = core.HasValue || memory.HasValue
                ? $"NVML P0 调整偏移：核心 {(core.HasValue ? core + " MHz" : "不可读")}；显存 {(memory.HasValue ? memory + " MHz" : "不可读")}。偏移为 0 不排除 BIOS 或曲线调整。"
                : "驱动未公开可验证的频率调整偏移；GPU 自动 Boost 不等于超频。";
            var overclock = new OverclockInfo(state, core, null, evidence, "NVML 不记录最后设置偏移的软件，无法确定调试软件。" );
            var memoryOverclock = overclock with { State = memory > 0 ? "Confirmed" : memory == 0 ? "NoOffset" : "Unknown", OffsetMHz = memory };
            result.Add(new(id, device.Name, sensors, memorySensors, overclock, memoryOverclock));
        }
        return result;
    }

    private int? ReadOffset(IntPtr device, uint type, GetOffset? older)
    {
        if (_clockOffsets is not null)
        {
            var offsets = new ClockOffset { Version = 24 | (1u << 24), Type = type, Pstate = 0 };
            if (_clockOffsets(device, ref offsets) == 0) return offsets.OffsetMHz;
        }
        return older is not null && older(device, out var value) == 0 ? value : null;
    }

    private static SensorReading Reading(string id, string key, string name, string kind, double? value, string unit, int status) =>
        new(id + "/" + key, name, kind, value, unit, "NVIDIA NVML", value.HasValue ? null : $"NVML 未返回数据（状态 {status}：{StatusName(status)}）。");
    private static string StatusName(int status) => status switch
    {
        1 => "未初始化", 2 => "参数不可用", 3 => "设备或驱动不支持", 4 => "权限不足", 6 => "设备未找到", 15 => "GPU 不可访问", _ => "驱动暂未提供"
    };
    private static string ReasonNames(ulong reasons)
    {
        var names = new List<string>();
        (ulong Bit, string Name)[] known = [(2, "应用时钟设置"), (4, "功率限制"), (8, "硬件降频"), (16, "同步 Boost"),
            (32, "软件温度限制"), (64, "硬件温度限制"), (128, "外部功率制动"), (256, "显示时钟设置")];
        foreach (var item in known) if ((reasons & item.Bit) != 0) names.Add(item.Name);
        if ((reasons & ~0x1FFUL) != 0) names.Add($"其他驱动事件 0x{reasons & ~0x1FFUL:x}");
        return names.Count > 0 ? string.Join("、", names) : "未报告（空闲降频单独显示）";
    }
    private T? Export<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(_library, name, out var pointer)
        ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : null;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHandle(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int GetName(IntPtr device, StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUnsigned(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTypedUnsigned(IntPtr device, uint type, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetOffset(IntPtr device, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMask(IntPtr device, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUtilization(IntPtr device, out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr device, out DeviceMemory value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetClockOffsets(IntPtr device, ref ClockOffset value);
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceMemory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct ClockOffset { public uint Version, Type, Pstate; public int OffsetMHz, MinMHz, MaxMHz; }
    public void Dispose()
    {
        _shutdown?.Invoke(); _shutdown = null;
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }
}
