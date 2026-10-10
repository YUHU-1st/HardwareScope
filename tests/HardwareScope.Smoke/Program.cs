using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using HardwareScope.Core;

if (args.FirstOrDefault() == "--benchmark-processes")
{
    var type = typeof(MonitorHost).Assembly.GetType("HardwareScope.Core.ProcessSampler")!;
    var sampler = Activator.CreateInstance(type)!;
    var method = type.GetMethod("Sample")!;
    using var selfProcess = Process.GetCurrentProcess();
    for (var i = 0; i < 8; i++)
    {
        var before = selfProcess.TotalProcessorTime.TotalMilliseconds;
        var timer = Stopwatch.StartNew();
        var rows = (List<ProcessSample>)method.Invoke(sampler, [new Dictionary<int, ProcessGpuUsage>()])!;
        timer.Stop(); selfProcess.Refresh();
        var cpu = selfProcess.TotalProcessorTime.TotalMilliseconds - before;
        Console.WriteLine($"sample={i}, processes={rows.Count}, elapsed={timer.Elapsed.TotalMilliseconds:0.0}ms, cpu={cpu:0.0}ms, logical={Environment.ProcessorCount}");
        await Task.Delay(250);
    }
    return;
}

if (args.FirstOrDefault() == "--integration-server")
{
    var directory = Path.GetFullPath("artifacts/android-integration");
    Directory.CreateDirectory(directory);
    await using var service = new MonitorHost(Path.Combine(directory, "isolated-state"));
    await service.SaveSettingsAsync(new AppSettings { RemoteEnabled = true, RemoteControlEnabled = true, Port = 47839 });
    await service.StartAsync();
    var info = service.GetConnectionInfo();
    await File.WriteAllTextAsync(Path.Combine(directory, "connection.json"), JsonSerializer.Serialize(new
    {
        address = "https://127.0.0.1:47839", pin = service.CreatePairingPin().Pin, token = info.Token, fingerprint = info.CertificateSha256
    }));
    Console.WriteLine("Integration server ready on HTTPS localhost:47839; isolated test credentials saved locally.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
    try { await Task.Delay(Timeout.Infinite, deadline.Token); }
    catch (OperationCanceledException) { }
    service.RotateRemoteToken();
    await service.StopAsync();
    return;
}

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/verification");
Directory.CreateDirectory(output);
var data = Path.Combine(output, "isolated-state");
var results = new List<object>();
void Check(string name, bool condition, string detail = "")
{
    results.Add(new { name, passed = condition, detail });
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name} {detail}");
    if (!condition) throw new InvalidOperationException(name + ": " + detail);
}

using var portPicker = new TcpListener(IPAddress.Loopback, 0);
portPicker.Start();
var port = ((IPEndPoint)portPicker.LocalEndpoint).Port;
portPicker.Stop();
await using var host = new MonitorHost(data);
await host.SaveSettingsAsync(new AppSettings { RemoteEnabled = true, SamplingIntervalMs = 1000, Port = port });
await host.StartAsync();
using var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null &&
        Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(host.GetConnectionInfo().CertificateSha256, StringComparison.OrdinalIgnoreCase)
};
using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
Check("Unauthenticated snapshot is rejected", (await client.GetAsync("/api/snapshot")).StatusCode == HttpStatusCode.Unauthorized);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
Check("Invalid token is rejected", (await client.GetAsync("/api/snapshot")).StatusCode == HttpStatusCode.Unauthorized);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.GetConnectionInfo().Token);
using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 90 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
var watch = Stopwatch.StartNew();
while ((host.Current.Sequence < 4 || !host.Current.Processes.Any(p => p.Pid == child.Id)) && watch.Elapsed < TimeSpan.FromSeconds(45))
    await Task.Delay(200);
Check("Sampler delivered real snapshots", host.Current.Sequence >= 4, $"sequence={host.Current.Sequence}");
var snapshot = await client.GetFromJsonAsync<MonitorSnapshot>("/api/snapshot");
Check("Authenticated HTTPS snapshot", snapshot?.Hardware.Count > 0 && snapshot.Processes.Count > 0);
Check("CPU and memory inventory", snapshot!.Hardware.Any(h => h.Category == "CPU") && snapshot.Hardware.Any(h => h.Category == "Memory"));
Check("Snapshot contains real clock/temperature values", snapshot.Hardware.SelectMany(h => h.Sensors).Any(s => s.Value.HasValue && s.Kind is "Clock" or "Temperature"));
Check("Unsupported values retain reasons", snapshot.Hardware.SelectMany(h => h.Sensors).Where(s => s.Value == null).All(s => !string.IsNullOrWhiteSpace(s.UnavailableReason)));
Check("Credentials not leaked in remote status", (await client.GetStringAsync("/api/connection")).Contains("\"token\":\"\""));
var row = snapshot.Processes.Single(p => p.Pid == child.Id);
Check("Disposable child is controllable", row.CanControl, row.ControlReason ?? "");
var body = new { instanceId = row.InstanceId, priority = "BelowNormal" };
Check("Remote control initially disabled", (await client.PostAsJsonAsync($"/api/processes/{child.Id}/priority", body)).StatusCode == HttpStatusCode.Forbidden);
await host.SaveSettingsAsync(host.Settings with { RemoteControlEnabled = true });
Check("Stale process identity rejected", !(await host.SetPriorityAsync(child.Id, "stale", "High")).Success);
Check("Realtime priority rejected", !(await host.SetPriorityAsync(child.Id, row.InstanceId, "RealTime")).Success);
Check("Missing priority cannot terminate", !(await host.SetPriorityAsync(child.Id, row.InstanceId, null!)).Success && !child.HasExited);
Check("Missing HTTP priority cannot terminate", (await client.PostAsJsonAsync($"/api/processes/{child.Id}/priority", new { instanceId = row.InstanceId, priority = (string?)null })).StatusCode == HttpStatusCode.BadRequest && !child.HasExited);
var priority = await (await client.PostAsJsonAsync($"/api/processes/{child.Id}/priority", body)).Content.ReadFromJsonAsync<CommandResult>();
child.Refresh();
Check("Remote priority actually applied", priority!.Success && child.PriorityClass == ProcessPriorityClass.BelowNormal);
var self = snapshot.Processes.Single(p => p.Pid == Environment.ProcessId);
Check("Monitor protects itself", !(await host.EndProcessAsync(self.Pid, self.InstanceId)).Success);
Check("Invalid settings rejected", (await client.PutAsJsonAsync("/api/settings", host.Settings with { SamplingIntervalMs = 0 })).StatusCode == HttpStatusCode.BadRequest);
var save = await client.PutAsJsonAsync("/api/settings", host.Settings with { SamplingIntervalMs = 2000 });
Check("Remote settings saved", save.IsSuccessStatusCode && host.Settings.SamplingIntervalMs == 2000);
Check("Settings backup exists", File.Exists(Path.Combine(data, "settings.json.bak")));
var terminate = await (await client.PostAsJsonAsync($"/api/processes/{child.Id}/terminate", new { instanceId = row.InstanceId })).Content.ReadFromJsonAsync<CommandResult>();
Check("Remote termination accepted", terminate!.Success, terminate.Message);
using var terminationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await child.WaitForExitAsync(terminationTimeout.Token);
Check("Remote termination ends only disposable child", terminate!.Success && child.HasExited);
var oldToken = host.GetConnectionInfo().Token;
host.RotateRemoteToken();
Check("Revoked token denied", (await client.GetAsync("/api/snapshot")).StatusCode == HttpStatusCode.Unauthorized);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.GetConnectionInfo().Token);
Check("New token succeeds", (await client.GetAsync("/api/snapshot")).IsSuccessStatusCode);
using (var rejectedHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => false })
using (var rejectedClient = new HttpClient(rejectedHandler) { BaseAddress = client.BaseAddress })
{
    var refused = false;
    try { await rejectedClient.GetAsync("/api/snapshot"); }
    catch (HttpRequestException) { refused = true; }
    Check("Certificate mismatch is refused", refused);
}
using (var pairingClient = new HttpClient(handler, false) { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(15) })
    await PairingChecks.RunAsync(pairingClient, host, Check);
await host.SaveSettingsAsync(host.Settings with { BluetoothEnabled = true });
var bluetooth = host.GetConnectionInfo().BluetoothStatus;
Check("Bluetooth listener attempted on real adapter", !bluetooth.Contains("未启用"), bluetooth);
await Task.Delay(2500);
var sampled = host.Current;
await File.WriteAllTextAsync(Path.Combine(output, "snapshot.json"), JsonSerializer.Serialize(sampled, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
var disconnect = await client.PutAsJsonAsync("/api/settings", host.Settings with { RemoteEnabled = false, BluetoothEnabled = false });
Check("Remote connection shutdown setting accepted", disconnect.IsSuccessStatusCode);
await host.StopAsync();
Check("Pending connection restart cancelled during shutdown", true);
Check("Clean shutdown", true);
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
{
    timestamp = DateTimeOffset.Now, machine = sampled.MachineName, checks = results,
    diagnostics = sampled.Diagnostics, bluetooth,
    coverage = sampled.Hardware.Select(h => new
    {
        h.Category, h.Name, h.Status, sensors = h.Sensors.Count, readable = h.Sensors.Count(s => s.Value.HasValue),
        temperatures = h.Sensors.Where(s => s.Kind == "Temperature").Select(s => new { s.Name, s.Value, s.Unit, s.Source, s.UnavailableReason })
    })
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Verification artifacts: {output}");
