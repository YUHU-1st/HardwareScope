namespace HardwareScope.Core;

public sealed record AppSettings
{
    public int SamplingIntervalMs { get; init; } = 2000;
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool RemoteEnabled { get; init; }
    public bool RemoteControlEnabled { get; init; }
    public bool BluetoothEnabled { get; init; }
    public int Port { get; init; } = 47831;
}

public sealed record MonitorSnapshot(DateTimeOffset Timestamp, string MachineName, bool IsAdministrator,
    long Sequence, List<HardwareDevice> Hardware, List<ProcessSample> Processes,
    List<TuningTool> TuningTools, List<string> Notices, SampleDiagnostics Diagnostics);
public sealed record HardwareDevice(string Id, string Category, string Name, string Status,
    List<SensorReading> Sensors, OverclockInfo Overclock);
public sealed record SensorReading(string Id, string Name, string Kind, double? Value,
    string Unit, string Source, string? UnavailableReason);
public sealed record OverclockInfo(string State, double? OffsetMHz, double? Percent,
    string Evidence, string Attribution);
public sealed record ProcessSample(int Pid, string InstanceId, string Name, string? Path,
    double? CpuPercent, double MemoryMb, double? GpuPercent, double? NpuPercent,
    double? VramMb, double? IoReadBytesPerSecond, double? IoWriteBytesPerSecond,
    double? NetworkBytesPerSecond, string Priority, bool CanControl, string? ControlReason);
public sealed record TuningTool(string Name, string Evidence, string Attribution);
public sealed record SampleDiagnostics(double SampleDurationMs, double MonitorCpuPercent,
    double MonitorMemoryMb, int IntervalMs);
public sealed record CommandResult(bool Success, string Message);
public sealed record ConnectionInfo(string[] Addresses, string Token, string CertificateSha256,
    string BluetoothServiceUuid, string BluetoothStatus, bool RemoteEnabled, bool RemoteControlEnabled);
public sealed record PriorityRequest(string InstanceId, string Priority);
public sealed record TerminateRequest(string InstanceId);
