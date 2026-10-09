using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HardwareScope.Core;

public sealed record ProcessGpuUsage(double? GpuPercent, double? NpuPercent, double? VramMb);

internal sealed class GpuCounters : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = [];
    private readonly HashSet<string> _npuLuids;
    private bool _primed;
    public Dictionary<int, ProcessGpuUsage> Processes { get; private set; } = [];
    public Dictionary<string, double> AdapterLoads { get; private set; } = [];
    public Dictionary<string, double> NpuAdapterLoads { get; private set; } = [];
    public double? NpuLoad { get; private set; }
    public string? UnavailableReason { get; }

    public GpuCounters(IEnumerable<string> npuLuids)
    {
        _npuLuids = npuLuids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            UnavailableReason = "Windows 无法创建性能计数器查询。";
            return;
        }
        Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        Add("memory", @"\GPU Process Memory(*)\Dedicated Usage");
        // Some MCDM drivers publish their own object; others appear under GPU Engine and are identified by DXCore LUID.
        Add("npu", @"\NPU Engine(*)\Utilization Percentage");
        Add("neural", @"\Neural Engine(*)\Utilization Percentage");
        if (!_counters.ContainsKey("gpu")) UnavailableReason = "驱动未公开 Windows GPU Engine 性能计数器。";
        PdhCollectQueryData(_query);
    }

    private void Add(string key, string path)
    {
        if (PdhAddEnglishCounterW(_query, path, 0, out var counter) == 0) _counters.Add(key, counter);
    }

    public void Collect()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return;
        if (!_primed) { _primed = true; return; }
        var gpu = Read("gpu");
        var npu = Read("npu").Concat(Read("neural")).ToList();
        var gpuRows = new List<EngineRow>();
        var npuRows = new List<EngineRow>();
        foreach (var item in gpu)
        {
            var row = ParseEngine(item.Name, item.Value);
            if (row is null) continue;
            (_npuLuids.Contains(row.Luid) ? npuRows : gpuRows).Add(row);
        }
        foreach (var item in npu)
        {
            if (ParseEngine(item.Name, item.Value) is { } row) npuRows.Add(row);
        }
        var gpuPerProcess = ProcessLoads(gpuRows);
        var npuPerProcess = ProcessLoads(npuRows);
        var memory = Read("memory")
            .Select(x => (Pid: ParsePid(x.Name), x.Value))
            .Where(x => x.Pid > 0)
            .GroupBy(x => x.Pid)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Value) / (1024 * 1024));
        Processes = gpuPerProcess.Keys.Concat(npuPerProcess.Keys).Concat(memory.Keys).Distinct()
            .ToDictionary(pid => pid, pid => new ProcessGpuUsage(
                gpuPerProcess.TryGetValue(pid, out var g) ? g : null,
                npuPerProcess.TryGetValue(pid, out var n) ? n : null,
                memory.TryGetValue(pid, out var m) ? m : null));
        AdapterLoads = DeviceLoads(gpuRows);
        NpuAdapterLoads = DeviceLoads(npuRows);
        NpuLoad = NpuAdapterLoads.Count > 0 ? NpuAdapterLoads.Values.Max() : null;
    }

    private static Dictionary<int, double> ProcessLoads(List<EngineRow> rows) => rows
        .GroupBy(r => (r.Pid, r.Luid, r.Physical))
        .Select(g => (g.Key.Pid, Load: g.GroupBy(r => r.Engine).Max(e => e.Sum(r => r.Load))))
        .GroupBy(x => x.Pid)
        .ToDictionary(g => g.Key, g => Math.Clamp(g.Sum(x => x.Load), 0, 100));

    private static Dictionary<string, double> DeviceLoads(List<EngineRow> rows) => rows
        .GroupBy(r => r.Luid)
        .ToDictionary(g => g.Key, g => Math.Clamp(g.GroupBy(r => (r.Physical, r.Engine)).Max(e => e.Sum(r => r.Load)), 0, 100), StringComparer.OrdinalIgnoreCase);

    private sealed record EngineRow(int Pid, string Luid, string Physical, string Engine, double Load);
    private static EngineRow? ParseEngine(string name, double load)
    {
        var match = Regex.Match(name, @"pid_(\d+)_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var pid)
            ? new(pid, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value, Math.Clamp(load, 0, 100)) : null;
    }
    private static int ParsePid(string name)
    {
        var match = Regex.Match(name, @"pid_(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var pid) ? pid : 0;
    }

    private List<(string Name, double Value)> Read(string key)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(key, out var counter)) return result;
        uint bytes = 0, count = 0;
        const uint moreData = 0x800007D2;
        if (PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, ref count, IntPtr.Zero) != moreData || bytes == 0) return result;
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, ref count, buffer) != 0) return result;
            var stride = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                if (item.Value.Status is 0 or 1 && double.IsFinite(item.Value.Double))
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? "", Math.Max(0, item.Value.Double)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, nuint userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = IntPtr.Zero; }
}
