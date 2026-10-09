using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HardwareScope.Core;

public sealed class MonitorHost : IAsyncDisposable
{
    private readonly SettingsStore store;
    private HardwareSampler? hardware;
    private NetworkProcessSampler? processNetwork;
    private readonly ProcessSampler processes = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly List<Task> connectionRestarts = [];
    private CancellationTokenSource? cancellation;
    private Task? samplingTask;
    private RemoteServer? remote;
    private BluetoothServer? bluetooth;
    private readonly X509Certificate2 certificate;
    private string token;
    private long sequence;
    private MonitorSnapshot current;
    private AppSettings settings;
    private string? remoteError;
    public event Action<MonitorSnapshot>? SnapshotUpdated;
    public AppSettings Settings => settings;
    public MonitorSnapshot Current => Volatile.Read(ref current);

    public MonitorHost(string? dataDirectory = null)
    {
        store = new SettingsStore(dataDirectory);
        settings = store.Load();
        ValidateSettings(settings);
        token = store.GetToken();
        certificate = store.GetCertificate();
        current = new(DateTimeOffset.Now, Environment.MachineName, IsAdministrator(), 0, [], [], [],
            ["正在读取硬件与进程，请稍候"], new(0, 0, 0, settings.SamplingIntervalMs));
    }

    public async Task StartAsync()
    {
        if (cancellation != null) return;
        cancellation = new();
        await RestartConnectionsAsync();
        // Driver initialization and all sampling remain on one background thread.
        samplingTask = Task.Run(async () =>
        {
            hardware = new HardwareSampler();
            processNetwork = new NetworkProcessSampler();
            using var self = Process.GetCurrentProcess();
            var oldCpu = self.TotalProcessorTime.TotalMilliseconds;
            var oldTimestamp = Stopwatch.GetTimestamp();
            while (!cancellation.IsCancellationRequested)
            {
                var timer = Stopwatch.StartNew();
                var devices = hardware.Sample();
                var rows = processes.Sample(hardware.GetProcessGpuUsage());
                var networkRates = processNetwork.ReadRates(rows.Select(p => p.Pid));
                if (networkRates != null)
                    rows = rows.Select(p => p with { NetworkBytesPerSecond = networkRates.GetValueOrDefault(p.Pid) }).ToList();
                var notices = new List<string>(hardware.Notices)
                {
                    processNetwork.Status,
                    "检测到调优工具正在运行不能证明超频由该工具设置；常规睿频不作为超频证据。"
                };
                if (!IsAdministrator()) notices.Add("当前未以管理员身份运行；部分主板、CPU 温度和功耗传感器可能不可读。");
                if (remoteError != null) notices.Add(remoteError);
                self.Refresh();
                var cpu = self.TotalProcessorTime.TotalMilliseconds;
                var now = Stopwatch.GetTimestamp();
                var duration = (now - oldTimestamp) * 1000d / Stopwatch.Frequency;
                var overhead = duration > 0 ? Math.Max(0, (cpu - oldCpu) * 100 / duration / Environment.ProcessorCount) : 0;
                oldCpu = cpu; oldTimestamp = now;
                var snapshot = new MonitorSnapshot(DateTimeOffset.Now, Environment.MachineName, IsAdministrator(),
                    Interlocked.Increment(ref sequence), devices, rows, DetectTuningTools(rows), notices,
                    new(timer.Elapsed.TotalMilliseconds, overhead, self.WorkingSet64 / 1048576d, settings.SamplingIntervalMs));
                Volatile.Write(ref current, snapshot);
                SnapshotUpdated?.Invoke(snapshot);
                try { await Task.Delay(settings.SamplingIntervalMs, cancellation.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public async Task StopAsync()
    {
        if (cancellation == null) return;
        await cancellation.CancelAsync();
        if (samplingTask != null) await samplingTask;
        if (remote != null) { await remote.DisposeAsync(); remote = null; }
        if (bluetooth != null) { await bluetooth.DisposeAsync(); bluetooth = null; }
        Task[] restarts;
        lock (connectionRestarts) restarts = connectionRestarts.ToArray();
        await Task.WhenAll(restarts);
        hardware?.Dispose(); hardware = null;
        if (processNetwork != null) { await processNetwork.DisposeAsync(); processNetwork = null; }
        cancellation.Dispose(); cancellation = null;
    }

    public Task SaveSettingsAsync(AppSettings updated) => SaveSettingsCoreAsync(updated, true);

    private async Task SaveSettingsCoreAsync(AppSettings updated, bool restartConnections)
    {
        ValidateSettings(updated);
        await settingsGate.WaitAsync();
        try
        {
            var old = settings;
            if (updated.StartWithWindows != old.StartWithWindows) SettingsStore.SetStartup(updated.StartWithWindows);
            store.Save(updated);
            Volatile.Write(ref settings, updated);
            if (restartConnections && cancellation != null && (old.RemoteEnabled != updated.RemoteEnabled || old.BluetoothEnabled != updated.BluetoothEnabled || old.Port != updated.Port))
                await RestartConnectionsAsync();
        }
        finally { settingsGate.Release(); }
    }

    private async Task RestartConnectionsAsync()
    {
        remoteError = null;
        if (remote != null) { await remote.DisposeAsync(); remote = null; }
        if (bluetooth != null) { await bluetooth.DisposeAsync(); bluetooth = null; }
        if (settings.RemoteEnabled)
        {
            remote = new RemoteServer(this, certificate);
            try { await remote.StartAsync(settings.Port); }
            catch (Exception e) when (e is IOException or InvalidOperationException or SocketException)
            { remoteError = $"局域网服务启动失败：{e.Message}"; await remote.DisposeAsync(); remote = null; }
        }
        if (settings.BluetoothEnabled)
        {
            bluetooth = new BluetoothServer(this);
            await bluetooth.StartAsync();
        }
    }

    public ConnectionInfo GetConnectionInfo()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => $"https://{a.Address}:{settings.Port}").Distinct().ToArray();
        return new(addresses, token, Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant(),
            BluetoothServer.ServiceUuid.ToString(), bluetooth?.Status ?? "蓝牙服务未启用", remote != null, settings.RemoteControlEnabled);
    }

    public void RotateRemoteToken() => Volatile.Write(ref token, store.RotateToken());

    internal bool Authorize(string? supplied)
    {
        if (supplied == null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token));
    }

    public Task<CommandResult> SetPriorityAsync(int pid, string instanceId, string priority) =>
        Task.FromResult(string.IsNullOrWhiteSpace(priority) ? new CommandResult(false, "缺少优先级") : processes.Control(pid, instanceId, priority));
    public Task<CommandResult> EndProcessAsync(int pid, string instanceId) => Task.FromResult(processes.Control(pid, instanceId, null));

    internal async Task<(int Status, object Body)> DispatchAsync(string method, string path, JsonElement? body)
    {
        if (method == "GET" && path == "/api/snapshot") return (200, Current);
        if (method == "GET" && path == "/api/settings") return (200, Settings);
        if (method == "GET" && path == "/api/connection")
        {
            var info = GetConnectionInfo();
            return (200, info with { Token = "" });
        }
        if (!Settings.RemoteControlEnabled) return (403, new CommandResult(false, "电脑端尚未允许手机执行控制操作"));
        if (method == "PUT" && path == "/api/settings")
        {
            if (body == null) return (400, new CommandResult(false, "缺少设置内容"));
            var updated = body.Value.Deserialize<AppSettings>(SettingsStore.Json)!;
            ValidateSettings(updated);
            var old = Settings;
            await SaveSettingsCoreAsync(updated, false);
            if (old.RemoteEnabled != updated.RemoteEnabled || old.BluetoothEnabled != updated.BluetoothEnabled || old.Port != updated.Port)
            {
                // Let the current response leave before rebinding its listener.
                var restartToken = cancellation?.Token ?? CancellationToken.None;
                var restart = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500, restartToken);
                        await settingsGate.WaitAsync(restartToken);
                        try { if (!restartToken.IsCancellationRequested) await RestartConnectionsAsync(); }
                        finally { settingsGate.Release(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) when (e is IOException or InvalidOperationException or SocketException)
                    { remoteError = "更新连接设置失败：" + e.Message; }
                });
                lock (connectionRestarts)
                {
                    connectionRestarts.RemoveAll(task => task.IsCompleted);
                    connectionRestarts.Add(restart);
                }
            }
            return (200, new CommandResult(true, "设置已保存；关闭连接服务或更改端口会断开手机"));
        }
        var pieces = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (method == "POST" && pieces.Length == 4 && pieces[0] == "api" && pieces[1] == "processes" && int.TryParse(pieces[2], out var pid) && body != null)
        {
            CommandResult result;
            if (pieces[3] == "priority")
            {
                var request = body.Value.Deserialize<PriorityRequest>(SettingsStore.Json);
                if (request == null) return (400, new CommandResult(false, "缺少优先级请求"));
                result = await SetPriorityAsync(pid, request.InstanceId, request.Priority);
            }
            else if (pieces[3] == "terminate")
            {
                var request = body.Value.Deserialize<TerminateRequest>(SettingsStore.Json);
                if (request == null) return (400, new CommandResult(false, "缺少进程实例标识"));
                result = await EndProcessAsync(pid, request.InstanceId);
            }
            else return (404, new CommandResult(false, "未找到操作"));
            return (result.Success ? 200 : 400, result);
        }
        return (404, new CommandResult(false, "未找到接口"));
    }

    private static void ValidateSettings(AppSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SamplingIntervalMs is < 1000 or > 10000) throw new ArgumentException("采样间隔必须在 1000–10000 毫秒之间");
        if (value.Port is < 1024 or > 65535) throw new ArgumentException("连接端口必须在 1024–65535 之间");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static List<TuningTool> DetectTuningTools(List<ProcessSample> rows)
    {
        var tools = new (string Prefix, string Name)[]
        {
            ("MSIAfterburner", "MSI Afterburner"), ("RTSS", "RivaTuner Statistics Server"),
            ("RyzenMaster", "AMD Ryzen Master"), ("AMDRyzenMaster", "AMD Ryzen Master"),
            ("XTU", "Intel Extreme Tuning Utility"), ("ThrottleStop", "ThrottleStop"),
            ("PrecisionX", "EVGA Precision X"), ("FanControl", "Fan Control"),
            ("LenovoLegionToolkit", "Lenovo Legion Toolkit"), ("GHelper", "G-Helper"),
            ("nvidiaInspector", "NVIDIA Inspector"), ("HWiNFO", "HWiNFO")
        };
        return tools.SelectMany(t => rows.Where(p => p.Name.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new TuningTool(t.Name, $"检测到正在运行的 {p.Name}，PID {p.Pid}",
                "仅为运行证据；驱动与固件没有统一接口记录当前参数的写入软件，不能据此确认超频来源"))).ToList();
    }

    public async ValueTask DisposeAsync() { await StopAsync(); certificate.Dispose(); settingsGate.Dispose(); }
}
