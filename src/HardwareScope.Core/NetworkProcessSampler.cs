using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace HardwareScope.Core;

internal sealed class NetworkProcessSampler : IAsyncDisposable
{
    private readonly object gate = new();
    private Dictionary<int, long> bytes = new();
    private TraceEventSession? session;
    private Task? pump;
    private long previousTimestamp = Stopwatch.GetTimestamp();
    private int lost;
    public string Status { get; private set; } = "分进程网络速率需要管理员权限；网卡总吞吐仍然可读。";

    public NetworkProcessSampler()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        try
        {
            session = new TraceEventSession("HardwareScope-Network-" + Environment.ProcessId) { StopOnDispose = true, BufferSizeMB = 2 };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var kernel = session.Source.Kernel;
            kernel.TcpIpSend += data => Add(data.ProcessID, data.size);
            kernel.TcpIpRecv += data => Add(data.ProcessID, data.size);
            kernel.TcpIpSendIPV6 += data => Add(data.ProcessID, data.size);
            kernel.TcpIpRecvIPV6 += data => Add(data.ProcessID, data.size);
            kernel.UdpIpSend += data => Add(data.ProcessID, data.size);
            kernel.UdpIpRecv += data => Add(data.ProcessID, data.size);
            kernel.UdpIpSendIPV6 += data => Add(data.ProcessID, data.size);
            kernel.UdpIpRecvIPV6 += data => Add(data.ProcessID, data.size);
            Status = "应用网络速率来自 Windows ETW 的 TCP/UDP IPv4/IPv6 收发字节，合计后按采样时间计算。";
            pump = Task.Run(() =>
            {
                try { session.Source.Process(); }
                catch (Exception e) when (e is Win32Exception or IOException)
                { Status = "网络事件采集已停止：" + e.Message; }
            });
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            session?.Dispose(); session = null;
            Status = "Windows 未允许启动分进程网络采集：" + e.Message;
        }
    }

    private void Add(int pid, int size)
    {
        if (pid <= 0 || size <= 0) return;
        lock (gate) bytes[pid] = bytes.GetValueOrDefault(pid) + size;
    }

    public IReadOnlyDictionary<int, double>? ReadRates(IEnumerable<int> pids)
    {
        if (session == null || pump?.IsCompleted == true) return null;
        Dictionary<int, long> window;
        lock (gate) { window = bytes; bytes = new(); }
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
        previousTimestamp = timestamp;
        var newLost = session.Source.EventsLost;
        if (newLost != lost)
        {
            lost = newLost;
            Status = "本次网络事件发生丢失，当前分进程网络值不完整，暂不显示。";
            return null;
        }
        return pids.ToDictionary(pid => pid, pid => window.GetValueOrDefault(pid) / Math.Max(elapsed, 0.001));
    }

    public async ValueTask DisposeAsync()
    {
        if (session == null) return;
        session.Source.StopProcessing();
        session.Dispose();
        if (pump != null) await pump;
        session = null;
    }
}
