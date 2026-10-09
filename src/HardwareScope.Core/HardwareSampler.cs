using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace HardwareScope.Core;

public sealed class HardwareSampler : IDisposable
{
    private readonly Computer _computer;
    private readonly NativeTelemetry _native = new();
    private readonly HardwareNvml _nvml;
    private readonly GpuCounters _gpuCounters;
    private readonly List<InventoryDevice> _inventory = [];
    private readonly List<HardwareNpuAdapter> _npus;
    private readonly List<MemoryModule> _memoryModules = [];
    private readonly Dictionary<string, NetworkSample> _networkPrevious = [];
    private readonly HashSet<string> _visibleNetworkIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadableCpuIds = [];
    private readonly bool _isAdministrator;
    private readonly int _pawnIoError;
    private readonly List<SensorReading> _dimmTemperatures = [];
    private readonly Dictionary<string, DiskTemperatureCache> _diskTemperatures = [];
    private readonly Dictionary<string, string> _storageTemperatureReasons = [];
    private NetworkInterface[] _networkInterfaces = [];
    private long _networkInventoryTimestamp;
    private DateTimeOffset _networkInventoryTime;
    private readonly Dictionary<string, long> _lhmSupplementUpdates = [];
    private readonly Dictionary<string, DateTimeOffset> _lhmSupplementTimes = [];
    private bool _lhmOpened;
    private bool _disposed;
    public List<string> Notices { get; } = [];
    private sealed record InventoryDevice(string Id, string Category, string Name, string? NativePath = null, uint? DiskIndex = null);
    private sealed record MemoryModule(string Name, ulong Capacity, uint? ConfiguredRate, uint? RatedRate);
    private sealed record NetworkSample(long In, long Out, long Timestamp);
    private sealed record DiskTemperatureCache(long Timestamp, DateTimeOffset Updated, NativeTelemetry.DiskTemperatures Snapshot);

    public HardwareSampler()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        _pawnIoError = NativeTelemetry.ProbePawnIoAccess();
        CacheInventory();
        _npus = HardwareDxCore.EnumerateNpus();
        _gpuCounters = new GpuCounters(_npus.Select(n => n.Luid));
        _nvml = new HardwareNvml();
        _computer = new Computer
        {
            IsCpuEnabled = _pawnIoError == 0, IsGpuEnabled = true, IsMemoryEnabled = _pawnIoError == 0,
            IsMotherboardEnabled = true, IsStorageEnabled = true, IsControllerEnabled = true,
            IsNetworkEnabled = false
        };
        try { _computer.Open(); _lhmOpened = true; }
        catch (Exception exception) { Notice("LibreHardwareMonitor 初始化失败：" + exception.Message); }
        if (_gpuCounters.UnavailableReason is { } counterReason) Notice(counterReason);
        if (_pawnIoError != 0) Notice(PawnIoUnavailableReason());
        if (!_isAdministrator)
            Notice("标准权限也读取 Windows CPU 占用率、内存、GPU 及驱动支持的 NVMe SMART 温度。CPU/主板/内存底层传感器受 PawnIO 设备访问权限限制，无需关闭 Windows 驱动安全设置。" );
        Notice("Windows 不提供可靠的“最后由哪个软件超频”审计记录；发现调试软件运行不能证明它修改了硬件。" );
    }

    public IReadOnlyDictionary<int, ProcessGpuUsage> GetProcessGpuUsage() => _gpuCounters.Processes;

    public List<HardwareDevice> Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var devices = new List<HardwareDevice>();
        _dimmTemperatures.Clear();
        _gpuCounters.Collect();
        var cpuLoad = _native.ReadCpuLoad();
        if (_lhmOpened)
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Memory && !hardware.Identifier.ToString().StartsWith("/memory/dimm/", StringComparison.Ordinal)) continue;
                Update(hardware);
                var category = Category(hardware.HardwareType);
                if (category is null) continue;
                var sensors = new List<SensorReading>();
                ReadSensors(hardware, sensors);
                if (category == "Memory")
                {
                    _dimmTemperatures.AddRange(sensors.Where(s => s.Kind == "Temperature" ||
                        s.Name.Contains("阈值", StringComparison.Ordinal) || s.Name.Contains("分辨率", StringComparison.Ordinal)).Select(s => s with { Name = hardware.Name + " · " + s.Name }));
                    continue;
                }
                if (category == "CPU" && !sensors.Any(s => s.Kind is "Clock" or "Temperature" && s.Value > 0))
                {
                    _unreadableCpuIds.Add(hardware.Identifier.ToString());
                    sensors = sensors.Where(s => s.Kind != "Load").Select(s => s with
                    {
                        Value = null, UnavailableReason = _pawnIoError != 0 ? PawnIoUnavailableReason() : "PawnIO 设备可访问，但当前处理器的底层模块未返回有效时钟或温度；采集器/模块可能不支持此型号，已停止无效轮询。"
                    }).ToList();
                    Notice("CPU 底层驱动未返回有效时钟或温度；已停止重复低层轮询，仍读取 Windows CPU 占用和电源管理频率。" );
                }
                devices.Add(new(hardware.Identifier.ToString(), category, hardware.Name, "Partial", sensors, UnknownOverclock(category)));
            }
        }
        foreach (var item in _inventory)
        {
            if (item.Category == "NPU" || devices.Any(d => d.Category == item.Category &&
                (item.Category == "Storage" ? MatchesStorage(d, item) : SameName(d.Name, item.Name) || item.Category is "CPU" or "Motherboard"))) continue;
            devices.Add(new(item.Id, item.Category, item.Name, "Unavailable", [], UnknownOverclock(item.Category)));
        }
        AddStorageTemperatures(devices);
        var cpu = devices.FirstOrDefault(d => d.Category == "CPU");
        if (cpu is not null)
        {
            cpu.Sensors.Insert(0, new(cpu.Id + "/system-load", "CPU 总占用率", "Load", cpuLoad, "%", "Windows GetSystemTimes",
                cpuLoad.HasValue ? null : "需要两个采样点计算 CPU 占用率。"));
            var frequency = NativeTelemetry.ReadCpuFrequencyMHz();
            cpu.Sensors.Add(new(cpu.Id + "/windows-frequency", "CPU 当前频率（Windows 电源管理报告）", "Clock", frequency, "MHz",
                "Windows CallNtPowerInformation / ProcessorInformation", frequency.HasValue ? null : "电源管理驱动未提供当前频率；此值是 OS 报告，可能不同于底层实测时钟。"));
        }
        var nvmlSnapshots = _nvml.Sample();
        var usedGpuIds = new HashSet<string>();
        foreach (var snapshot in nvmlSnapshots)
        {
            var match = devices.FirstOrDefault(d => d.Category == "GPU" && !usedGpuIds.Contains(d.Id) && SameName(d.Name, snapshot.Name));
            if (match is not null)
            {
                usedGpuIds.Add(match.Id);
                var index = devices.IndexOf(match);
                var additional = match.Sensors.Where(s => s.Name is not "GPU Core" and not "GPU Memory Controller" and not "GPU Package" and
                    not "GPU Memory Total" and not "GPU Memory Free" and not "GPU Memory Used" and not "GPU Memory" and not "D3D Dedicated Memory Used");
                devices[index] = match with { Sensors = snapshot.Sensors.Concat(additional).ToList(), Overclock = snapshot.Overclock };
            }
            else devices.Add(new(snapshot.Id, "GPU", snapshot.Name, "Partial", snapshot.Sensors, snapshot.Overclock));
            var memoryTemperatures = match?.Sensors.Where(s => s.Kind == "Temperature" && s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) ?? [];
            devices.Add(new(snapshot.Id + "/vram", "VRAM", snapshot.Name + " · 显存", "Partial", snapshot.MemorySensors.Concat(memoryTemperatures).ToList(),
                snapshot.MemoryOverclock));
        }
        foreach (var gpu in devices.Where(d => d.Category == "GPU").ToList())
        {
            if (devices.Any(d => d.Category == "VRAM" && SameName(d.Name.Replace(" · 显存", ""), gpu.Name))) continue;
            var memorySensors = gpu.Sensors.Where(s => s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("显存")).ToList();
            devices.Add(new(gpu.Id + "/vram", "VRAM", gpu.Name + " · 显存", "Partial", memorySensors, UnknownOverclock("VRAM")));
        }
        AddMemory(devices);
        AddNpus(devices);
        AddNetworks(devices);
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            RequiredSensors(device);
            devices[i] = device with { Status = device.Status == "Disconnected" ? "Disconnected" :
                device.Sensors.All(s => s.Value is null) ? "Unavailable" :
                device.Sensors.Any(s => s.Value is null) ? "Partial" : "OK" };
        }
        if (devices.Where(d => d.Category == "CPU").SelectMany(d => d.Sensors).All(s => s.Kind != "Temperature" || s.Value is null))
            Notice(_pawnIoError != 0 ? PawnIoUnavailableReason() : "PawnIO 设备可访问，但本机 CPU 温度未返回；处理器底层模块或型号支持不足，不可用项不以 0 替代。" );
        return devices;
    }

    private void Update(IHardware hardware)
    {
        if (_unreadableCpuIds.Contains(hardware.Identifier.ToString())) return;
        if (hardware.HardwareType.ToString() == "GpuNvidia" || hardware.HardwareType == HardwareType.Memory)
        {
            var id = hardware.Identifier.ToString();
            var now = Stopwatch.GetTimestamp();
            if (_lhmSupplementUpdates.TryGetValue(id, out var previous) && (now - previous) / (double)Stopwatch.Frequency < 10) return;
            _lhmSupplementUpdates[id] = now;
            _lhmSupplementTimes[id] = DateTimeOffset.Now;
        }
        try { hardware.Update(); }
        catch (Exception exception) { Notice(hardware.Name + " 更新失败：" + exception.Message); }
        foreach (var child in hardware.SubHardware) Update(child);
    }

    private void ReadSensors(IHardware hardware, List<SensorReading> readings)
    {
        foreach (var sensor in hardware.Sensors)
        {
            var type = sensor.SensorType.ToString();
            var (kind, unit) = type switch
            {
                "Load" => ("Load", "%"), "Clock" => ("Clock", "MHz"), "Temperature" => ("Temperature", "°C"),
                "Power" => ("Power", "W"), "Fan" => ("Fan", "RPM"), "Data" => ("Data", "GiB"),
                "SmallData" => ("Data", "MiB"), "Throughput" => ("Throughput", "B/s"),
                "Voltage" => ("Other", "V"), "Current" => ("Other", "A"), "Frequency" => ("Clock", "Hz"),
                "Control" or "Level" => ("Other", "%"), "Timing" => ("Other", "ns"),
                "TimeSpan" => ("Other", "s"), "Flow" => ("Other", "L/h"), "Energy" => ("Other", "mWh"),
                _ => ("Other", "")
            };
            if (type == "Factor" && (sensor.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Throttl", StringComparison.OrdinalIgnoreCase))) kind = "Limit";
            var name = sensor.Name;
            if (type == "Temperature" && (name.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Critical", StringComparison.OrdinalIgnoreCase) || name.Contains("Limit", StringComparison.OrdinalIgnoreCase)))
            {
                kind = "Other";
                name += "（温度阈值）";
            }
            else if (type == "Temperature" && name.Contains("Resolution", StringComparison.OrdinalIgnoreCase))
            {
                kind = "Other";
                name += "（测温分辨率）";
            }
            var value = sensor.Value.HasValue && float.IsFinite(sensor.Value.Value) ? (double?)sensor.Value.Value : null;
            var source = _lhmSupplementTimes.TryGetValue(hardware.Identifier.ToString(), out var updated)
                ? $"LibreHardwareMonitor 0.9.6{(hardware.HardwareType == HardwareType.Memory ? " / DIMM SPD 温度传感器" : "")}（每 10 秒刷新，上次 {updated:yyyy-MM-dd HH:mm:ss}）" : "LibreHardwareMonitor 0.9.6";
            readings.Add(new(sensor.Identifier.ToString(), name, kind, value, unit, source,
                value.HasValue ? null : MissingSensorReason(Category(hardware.HardwareType) ?? "Motherboard", kind)));
            // The application owns chart history; disable the library's additional 24-hour sensor history buffer.
            sensor.ValuesTimeWindow = TimeSpan.Zero;
        }
        foreach (var child in hardware.SubHardware) ReadSensors(child, readings);
    }

    private void AddMemory(List<HardwareDevice> devices)
    {
        const string id = "windows/memory";
        var sensors = new List<SensorReading>();
        var mem = NativeTelemetry.ReadMemory();
        sensors.Add(new(id + "/load", "内存占用率", "Load", mem?.Load, "%", "Windows GlobalMemoryStatusEx", mem is null ? "系统未返回内存信息。" : null));
        sensors.Add(new(id + "/used", "已用物理内存", "Data", mem is null ? null : (mem.Value.Total - mem.Value.Available) / 1073741824.0, "GiB", "Windows GlobalMemoryStatusEx", mem is null ? "系统未返回内存信息。" : null));
        sensors.Add(new(id + "/total", "可用物理内存容量", "Data", mem?.Total / 1073741824.0, "GiB", "Windows GlobalMemoryStatusEx", mem is null ? "系统未返回内存信息。" : null));
        for (var i = 0; i < _memoryModules.Count; i++)
        {
            var module = _memoryModules[i];
            sensors.Add(new(id + $"/module{i}/capacity", module.Name + " 容量", "Data", module.Capacity / 1073741824.0, "GiB", "WMI Win32_PhysicalMemory", null));
            sensors.Add(new(id + $"/module{i}/configured", module.Name + " 固件配置传输率", "Other", module.ConfiguredRate, "MT/s", "WMI Win32_PhysicalMemory.ConfiguredClockSpeed",
                module.ConfiguredRate is null ? "固件未提供配置值；此字段不是实时 DRAM 时钟测量。" : null));
            sensors.Add(new(id + $"/module{i}/rated", module.Name + " 固件报告标称传输率", "Other", module.RatedRate, "MT/s", "WMI Win32_PhysicalMemory.Speed", module.RatedRate is null ? "固件未提供标称值。" : null));
        }
        sensors.AddRange(_dimmTemperatures);
        var description = _memoryModules.Count > 0 ? "物理内存（" + _memoryModules.Count + " 个模组）" : "物理内存";
        devices.Add(new(id, "Memory", description, "Partial", sensors, UnknownOverclock("Memory")));
    }

    private void AddNpus(List<HardwareDevice> devices)
    {
        if (_npus.Count > 0)
        {
            foreach (var npu in _npus)
            {
                double? load = _gpuCounters.NpuAdapterLoads.TryGetValue(npu.Luid, out var usage) ? usage : null;
                var sensors = new List<SensorReading>
                {
                    new(npu.Id + "/load", "NPU 占用率", "Load", load, "%", "Windows PDH / DXCore NPU LUID",
                        load.HasValue ? null : "已检测到 NPU，但驱动尚未公开可读的引擎占用率。")
                };
                devices.Add(new(npu.Id, "NPU", npu.Name, "Partial", sensors, UnknownOverclock("NPU")));
            }
            return;
        }
        var inventoryNpus = _inventory.Where(d => d.Category == "NPU").ToList();
        foreach (var npu in inventoryNpus)
            devices.Add(new(npu.Id, "NPU", npu.Name, "Unavailable", [], UnknownOverclock("NPU")));
        if (inventoryNpus.Count == 0)
            devices.Add(new("windows/npu-unavailable", "NPU", "NPU（未检测到公开设备）", "Unavailable", [], UnknownOverclock("NPU")));
        Notice("NPU 数据取决于 DXCore/MCDM 驱动公开能力；未检测到设备或计数器时显示不可用，不将 GPU AI 引擎自动当作 NPU。" );
    }

    private void AddNetworks(List<HardwareDevice> devices)
    {
        var now = Stopwatch.GetTimestamp();
        if (_networkInventoryTimestamp == 0 || (now - _networkInventoryTimestamp) / (double)Stopwatch.Frequency >= 30)
        {
            if (_networkInventoryTimestamp != 0) RefreshVisibleNetworkIds();
            try
            {
                _networkInterfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    (_visibleNetworkIds.Contains(n.Id) || _visibleNetworkIds.Count == 0 && n.OperationalStatus == OperationalStatus.Up)).ToArray();
                _networkInventoryTimestamp = now;
                _networkInventoryTime = DateTimeOffset.Now;
            }
            catch (NetworkInformationException exception) { Notice("网络接口清单刷新失败：" + exception.Message); }
        }
        foreach (var nic in _networkInterfaces)
        {
            try
            {
                var stats = nic.GetIPv4Statistics();
                var id = "network/" + nic.Id;
                double? received = null, sent = null;
                if (_networkPrevious.TryGetValue(nic.Id, out var previous) && now > previous.Timestamp && stats.BytesReceived >= previous.In && stats.BytesSent >= previous.Out)
                {
                    var seconds = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
                    received = (stats.BytesReceived - previous.In) / seconds;
                    sent = (stats.BytesSent - previous.Out) / seconds;
                }
                _networkPrevious[nic.Id] = new(stats.BytesReceived, stats.BytesSent, now);
                var speed = nic.Speed;
                var online = nic.OperationalStatus == OperationalStatus.Up;
                var source = "Windows NetworkInterface IP statistics";
                var sensors = new List<SensorReading>
                {
                    new(id + "/receive", "接收速率", "Throughput", received, "B/s", source, received is null ? "需要两个采样点计算速率。" : null),
                    new(id + "/send", "发送速率", "Throughput", sent, "B/s", source, sent is null ? "需要两个采样点计算速率。" : null),
                    new(id + "/link", "链路协商速率（非芯片时钟）", "Other", speed > 0 && online ? speed / 1000000.0 : null, "Mbps",
                        $"Windows NetworkInterface.Speed（30 秒刷新，上次 {_networkInventoryTime:HH:mm:ss}）", speed > 0 && online ? null : "网卡未连接或驱动未提供链路速率。"),
                    new(id + "/load", "链路峰向占用率", "Load", received.HasValue && sent.HasValue && speed > 0 && online ? Math.Clamp(Math.Max(received.Value, sent.Value) * 8 / speed * 100, 0, 100) : null,
                        "%", "字节速率 / 协商链路速率（发送、接收取较高者）", received.HasValue && sent.HasValue && speed > 0 && online ? null : "未取得速率或连接信息。")
                };
                devices.Add(new(id, "Network", nic.Name + " · " + nic.Description, online ? "Partial" : "Disconnected", sensors, UnknownOverclock("Network")));
            }
            catch (NetworkInformationException exception) { Notice(nic.Name + " 网络统计不可用：" + exception.Message); }
        }
        Notice("支持 NVML 的 GPU 主指标每次采样更新；GPU 补充温度、NVMe SMART 温度和 DIMM SPD 温度每 10 秒刷新。网卡字节速率实时采样，接口、连接状态和链路协商信息每 30 秒刷新以支持热插拔。" );
    }

    private void RequiredSensors(HardwareDevice device)
    {
        (string Kind, string Name, string Unit)[] required = [("Load", "占用率", "%"), ("Clock", "实时频率", "MHz"),
            ("Temperature", "温度", "°C"), ("Power", "功耗", "W"), ("Fan", "风扇转速", "RPM"), ("Limit", "性能限制状态", "")];
        foreach (var item in required)
        {
            if (device.Sensors.Any(s => s.Kind == item.Kind)) continue;
            var reason = device.Category == "Storage" && item.Kind == "Temperature" && _storageTemperatureReasons.TryGetValue(device.Id, out var storageReason)
                ? storageReason : MissingSensorReason(device.Category, item.Kind);
            device.Sensors.Add(new(device.Id + "/unavailable/" + item.Kind.ToLowerInvariant(), item.Name, item.Kind, null, item.Unit, "设备能力", reason));
        }
    }

    private bool MatchesStorage(HardwareDevice device, InventoryDevice disk)
    {
        if (device.Id == disk.Id) return true;
        if (disk.DiskIndex.HasValue && Regex.IsMatch(device.Id, @"^/(nvme|hdd|ssd)/\d+$"))
            return device.Id.EndsWith("/" + disk.DiskIndex.Value, StringComparison.Ordinal);
        return _inventory.Count(i => i.Category == "Storage" && SameName(i.Name, disk.Name)) == 1 && SameName(device.Name, disk.Name);
    }

    private void AddStorageTemperatures(List<HardwareDevice> devices)
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var disk in _inventory.Where(i => i.Category == "Storage" && !string.IsNullOrEmpty(i.NativePath)))
        {
            var device = devices.FirstOrDefault(d => d.Category == "Storage" && MatchesStorage(d, disk));
            if (device is null) continue;
            if (!_diskTemperatures.TryGetValue(disk.NativePath!, out var cached) || (now - cached.Timestamp) / (double)Stopwatch.Frequency >= 10)
            {
                cached = new(now, DateTimeOffset.Now, NativeTelemetry.ReadNvmeTemperatures(disk.NativePath!));
                _diskTemperatures[disk.NativePath!] = cached;
            }
            if (cached.Snapshot.Readings.Count == 0)
            {
                _storageTemperatureReasons[device.Id] = cached.Snapshot.UnavailableReason!;
                continue;
            }
            _storageTemperatureReasons.Remove(device.Id);
            // Prefer the permission-independent SMART source; keep warning/critical thresholds as Other.
            device.Sensors.RemoveAll(s => s.Kind == "Temperature");
            var source = $"Windows NVMe SMART / {disk.NativePath}（只读；每 10 秒刷新，上次 {cached.Updated:yyyy-MM-dd HH:mm:ss}）";
            for (var index = 0; index < cached.Snapshot.Readings.Count; index++)
            {
                var temperature = cached.Snapshot.Readings[index];
                device.Sensors.Add(new(device.Id + "/nvme-smart/temperature" + index, temperature.Name, "Temperature", temperature.Celsius, "°C", source, null));
            }
        }
    }

    private string PawnIoUnavailableReason() => _pawnIoError switch
    {
        5 => "PawnIO 驱动设备已存在，但当前进程无权访问（Windows 错误 5，访问被拒绝）；CPU/主板/内存底层温度等需要以管理员身份启动后读取。无需安装新驱动或关闭系统保护。",
        2 or 3 => "未能打开 PawnIO 驱动设备（Windows 错误 " + _pawnIoError + "）；可能尚未安装或服务未运行，CPU/主板/内存底层传感器不可读。",
        _ => "未能访问 PawnIO 驱动设备（Windows 错误 " + _pawnIoError + "）；请检查驱动状态及系统提供的错误信息，不要关闭系统保护。"
    };

    private string MissingSensorReason(string category, string kind)
    {
        if (category == "CPU")
        {
            if (kind == "Fan") return "处理器未公开独立风扇转速；笔记本风扇一般由厂商 EC 控制，需受支持的 EC/主板接口。";
            if (_pawnIoError != 0 && kind is "Clock" or "Temperature" or "Power" or "Limit") return PawnIoUnavailableReason();
            return "PawnIO 设备可访问，但当前处理器/模块未提供此指标；管理员权限不能补足未受支持的硬件接口。";
        }
        if (category == "Motherboard")
        {
            if (kind is "Load" or "Clock") return "主板没有统一的整体占用率或实时频率；请查看各部件的对应指标。";
            if (_pawnIoError != 0) return PawnIoUnavailableReason();
            return "PawnIO 设备可访问，但本机主板/厂商 EC 未向当前采集器公开此传感器；管理员权限不能补足未受支持的厂商接口。";
        }
        if (category == "Memory")
        {
            if (kind == "Temperature") return _pawnIoError != 0 ? PawnIoUnavailableReason() :
                "已启用 DIMM SPD 实际温度采集，但尚未取得有效读数；SMBus 路径、内存模组温度传感器或本机平台未被公开/支持。";
            if (kind == "Clock") return "固件只提供配置传输率；未取得实时 DRAM 时钟，MT/s 不作为 MHz 实测值。";
            if (kind == "Fan") return "内存模组未公开独立风扇转速。";
            return "内存/平台未公开此硬件指标；内存占用率与配置传输率不能推算功耗或性能限制。";
        }
        if (category == "Network") return kind == "Clock" ?
            "Windows 提供链路速率，未提供网卡芯片频率；链路速率不能替代芯片频率。" :
            "网卡驱动未公开此硬件传感器；Windows 网络统计仅提供流量及链路信息，管理员权限不保证可读取温度、功耗、风扇或性能限制。";
        if (category == "Storage") return kind == "Temperature" ?
            "存储驱动尚未提供有效的真实温度；设备协议或 USB 桥接接口可能不公开 SMART 传感器。" :
            "存储设备/驱动未公开此指标；SMART 温度与健康阈值不能推算时钟、功耗、风扇或性能限制。";
        if (category == "VRAM") return kind == "Temperature" ?
            "GPU 驱动未公开显存温度传感器；GPU 核心温度不能替代显存温度。" : kind == "Fan" ?
            "显存未公开独立风扇转速，GPU 风扇不作为显存独立风扇。" :
            "GPU 驱动未公开显存的此项独立指标；GPU 整体功耗或限制状态不能替代显存独立测量。";
        if (category == "NPU") return "未检测到可读的 NPU 传感器；此指标取决于设备和 DXCore/MCDM 驱动公开支持。";
        return "GPU 硬件或驱动未公开此传感器；管理员权限不能补足驱动不支持的指标。";
    }

    private void CacheInventory()
    {
        Query("SELECT DeviceID, Name FROM Win32_Processor", row => _inventory.Add(new("wmi/cpu/" + row["DeviceID"], "CPU", Text(row["Name"], "CPU"))));
        Query("SELECT Product, Manufacturer FROM Win32_BaseBoard", row => _inventory.Add(new("wmi/motherboard", "Motherboard", Text(row["Manufacturer"], "") + " " + Text(row["Product"], "主板"))));
        Query("SELECT PNPDeviceID, Name FROM Win32_VideoController", row =>
        {
            var pnp = Text(row["PNPDeviceID"], "");
            if (pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase))
                _inventory.Add(new("wmi/gpu/" + pnp, "GPU", Text(row["Name"], "GPU")));
        });
        Query("SELECT DeviceID, Index, Model FROM Win32_DiskDrive", row => _inventory.Add(new("wmi/storage/" + row["DeviceID"], "Storage", Text(row["Model"], "存储设备"),
            Text(row["DeviceID"], ""), uint.TryParse(row["Index"]?.ToString(), out var diskIndex) ? diskIndex : null)));
        Query("SELECT DeviceLocator, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory", row =>
            _memoryModules.Add(new(Text(row["DeviceLocator"], "DIMM") + " " + Text(row["PartNumber"], ""), Convert.ToUInt64(row["Capacity"] ?? 0),
                PositiveUint(row["ConfiguredClockSpeed"]), PositiveUint(row["Speed"]))));
        Query("SELECT PNPDeviceID, Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator' OR Name LIKE '%Neural%' OR Name LIKE '%NPU%' OR Name LIKE '%AI Boost%' OR Name LIKE '%AMD IPU%'", row =>
        {
            var name = Text(row["Name"], "计算加速器");
            if (Text(row["PNPClass"], "") == "ComputeAccelerator" || Regex.IsMatch(name, @"\bNPU\b|Neural|AI Boost|AMD IPU", RegexOptions.IgnoreCase))
                _inventory.Add(new("wmi/npu/" + row["PNPDeviceID"], "NPU", name));
        });
        RefreshVisibleNetworkIds();
    }
    private void RefreshVisibleNetworkIds()
    {
        _visibleNetworkIds.Clear();
        Query("SELECT GUID FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL", row =>
        {
            var guid = Text(row["GUID"], "");
            if (guid.Length > 0) _visibleNetworkIds.Add(guid);
        });
    }
    private void Query(string query, Action<ManagementBaseObject> read)
    {
        try
        {
            using var search = new ManagementObjectSearcher(query);
            using var rows = search.Get();
            foreach (ManagementBaseObject row in rows) { using (row) read(row); }
        }
        catch (Exception exception) when (exception is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { Notice("硬件清单部分不可用：" + exception.Message); }
    }
    private static string Text(object? value, string alternative) => string.IsNullOrWhiteSpace(value?.ToString()) ? alternative : value.ToString()!.Trim();
    private static uint? PositiveUint(object? value) => uint.TryParse(value?.ToString(), out var number) && number > 0 ? number : null;
    private static bool SameName(string left, string right) => left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase) ||
        left.Contains(right.Trim(), StringComparison.OrdinalIgnoreCase) || right.Contains(left.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? Category(HardwareType type) => type.ToString() switch
    {
        "Cpu" => "CPU", "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU", "Memory" => "Memory",
        "Motherboard" or "SuperIO" or "Cooler" or "Psu" or "PowerMonitor" => "Motherboard",
        "Storage" => "Storage", "Network" => "Network", _ => null
    };
    private static OverclockInfo UnknownOverclock(string category) => new("Unknown", null, null,
        category == "CPU" ? "当前频率高于基础频率可能为处理器正常 Boost；未取得可验证的 BIOS/PBO/BCLK 调整证据。" :
        category is "Memory" or "VRAM" ? "仅凭运行频率或固件配置速度不能确定是否超频；未取得可验证的配置档与调整记录。" :
        "设备未公开可验证的超频调整记录。", "无法可靠归因；运行中的调试软件只作为线索显示。" );
    private void Notice(string notice) { if (!Notices.Contains(notice)) Notices.Add(notice); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpuCounters.Dispose(); _nvml.Dispose();
        if (_lhmOpened) _computer.Close();
    }
}
