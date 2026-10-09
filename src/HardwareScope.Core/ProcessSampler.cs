using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HardwareScope.Core;

internal sealed class ProcessSampler
{
    private sealed record Previous(string Identity, double CpuMs, ulong Read, ulong Write, long Timestamp, string? Path);
    private Dictionary<int, Previous> previous = new();
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;

    public List<ProcessSample> Sample(IReadOnlyDictionary<int, ProcessGpuUsage> gpu)
    {
        var current = new Dictionary<int, Previous>();
        var rows = new List<ProcessSample>();
        var now = Stopwatch.GetTimestamp();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    if (pid == 0) continue;
                    var identity = Identity(process);
                    var cpu = process.TotalProcessorTime.TotalMilliseconds;
                    var memory = process.WorkingSet64 / 1048576d;
                    using var handle = OpenProcess(0x1000, false, pid);
                    var io = new IoCounters();
                    var ioAvailable = !handle.IsInvalid && GetProcessIoCounters(handle, out io);
                    double? cpuPercent = null, read = null, write = null;
                    if (previous.TryGetValue(pid, out var old) && old.Identity == identity)
                    {
                        var elapsed = (now - old.Timestamp) * 1000d / Stopwatch.Frequency;
                        if (elapsed > 0)
                        {
                            cpuPercent = Math.Clamp((cpu - old.CpuMs) * 100 / elapsed / Environment.ProcessorCount, 0, 100);
                            if (ioAvailable)
                            {
                                read = io.ReadTransferCount >= old.Read ? (io.ReadTransferCount - old.Read) * 1000d / elapsed : null;
                                write = io.WriteTransferCount >= old.Write ? (io.WriteTransferCount - old.Write) * 1000d / elapsed : null;
                            }
                        }
                    }
                    string? path = null;
                    if (previous.TryGetValue(pid, out var cached) && cached.Identity == identity) path = cached.Path;
                    else try { path = process.MainModule?.FileName; } catch (Win32Exception) { }
                    current[pid] = new(identity, cpu, io.ReadTransferCount, io.WriteTransferCount, now, path);
                    var canControl = CanControl(process, out var reason);
                    gpu.TryGetValue(pid, out var usage);
                    rows.Add(new(pid, identity, process.ProcessName, path, cpuPercent, memory,
                        usage?.GpuPercent, usage?.NpuPercent, usage?.VramMb, read, write, null,
                        process.PriorityClass.ToString(), canControl, reason));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Windows processes may exit or deny access while being sampled.
                }
            }
        }
        previous = current;
        return rows;
    }

    public static string Identity(Process process) => process.Id.ToString(CultureInfo.InvariantCulture) + ":" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    private bool CanControl(Process process, out string? reason)
    {
        if (process.Id == Environment.ProcessId) { reason = "监控软件自身受到保护"; return false; }
        if (process.Id <= 4 || process.SessionId != sessionId)
        { reason = "系统进程或其他登录会话受到保护"; return false; }
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid || !IsProcessCritical(handle, out var critical))
        { reason = "Windows 未允许验证此进程是否可以安全操作"; return false; }
        if (critical) { reason = "Windows 关键进程受到保护"; return false; }
        reason = null;
        return true;
    }

    public CommandResult Control(int pid, string identity, string? priority)
    {
        if (string.IsNullOrWhiteSpace(identity)) return new(false, "缺少进程实例标识，请刷新进程列表");
        var allowed = new[] { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" };
        if (priority != null && !allowed.Contains(priority, StringComparer.Ordinal))
            return new(false, "仅支持空闲、低于正常、正常、高于正常和高优先级");
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Identity(process) != identity) return new(false, "进程已重新启动，请刷新后重新选择");
            if (!CanControl(process, out var reason)) return new(false, reason!);
            if (priority != null)
            {
                process.PriorityClass = Enum.Parse<ProcessPriorityClass>(priority);
                return new(true, "进程优先级已更新");
            }
            process.Kill(entireProcessTree: false);
            return new(true, "已结束所选进程");
        }
        catch (ArgumentException) { return new(false, "进程已经退出"); }
        catch (InvalidOperationException) { return new(false, "进程已经退出"); }
        catch (Win32Exception e) { return new(false, $"Windows 拒绝操作：{e.Message}；可尝试以管理员身份运行"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle handle, out IoCounters counters);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}
