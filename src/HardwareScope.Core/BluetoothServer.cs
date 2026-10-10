using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InTheHand.Net;
using InTheHand.Net.Sockets;

namespace HardwareScope.Core;

internal sealed class BluetoothServer(MonitorHost host) : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = new("9174d900-1895-4d3e-a219-3a1927f05b81");
    private BluetoothListener? listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<int, BluetoothClient> clients = new();
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private Task? acceptTask;
    private int nextId;
    public string Status { get; private set; } = "蓝牙服务未启动";

    public Task StartAsync()
    {
        try
        {
            listener = new BluetoothListener(ServiceUuid) { ServiceName = "HardwareScope 硬件视界" };
            listener.Start();
            Status = "蓝牙服务已就绪，请在系统设置中配对手机";
            acceptTask = Task.Run(AcceptLoop);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException or InvalidOperationException)
        { Status = $"蓝牙不可用：{e.Message}"; listener?.Dispose(); listener = null; }
        return Task.CompletedTask;
    }

    private async Task AcceptLoop()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var client = await listener!.AcceptBluetoothClientAsync();
                try
                {
                    var endpoint = (BluetoothEndPoint)client.Client.RemoteEndPoint!;
                    if (!new BluetoothDeviceInfo(endpoint.Address).Authenticated) { client.Dispose(); continue; }
                    client.Authenticate = true;
                    client.Encrypt = true;
                }
                catch (SocketException) { client.Dispose(); continue; }
                if (clients.Count >= 4) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextId);
                clients[id] = client;
                sessions[id] = ServeClient(id, client);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (!cancellation.IsCancellationRequested) Status = $"蓝牙监听停止：{e.Message}";
                break;
            }
        }
    }

    private async Task ServeClient(int clientId, BluetoothClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
            while (!cancellation.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await ReadBoundedLine(reader, deadline.Token);
                if (line == null) break;
                string? id = null;
                int status;
                object body;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    id = root.TryGetProperty("id", out var requestId) ? requestId.GetString() : null;
                    var method = root.GetProperty("method").GetString() ?? throw new JsonException("缺少请求方法");
                    var path = root.GetProperty("path").GetString() ?? throw new JsonException("缺少请求路径");
                    JsonElement? requestBody = root.TryGetProperty("body", out var b) ? b.Clone() : null;
                    var supplied = root.TryGetProperty("token", out var auth) ? auth.GetString() : null;
                    if (path.StartsWith("/pair/", StringComparison.Ordinal))
                        (status, body) = await host.DispatchAsync(method, path, requestBody);
                    else if (!host.Authorize(supplied))
                    { status = 401; body = new CommandResult(false, "授权密钥无效"); }
                    else
                    {
                        (status, body) = await host.DispatchAsync(method, path, requestBody);
                    }
                }
                catch (Exception e) when (e is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
                { status = 400; body = new CommandResult(false, "请求格式错误：" + e.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, status, body }, SettingsStore.Json));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
        { /* A phone can disconnect at any time. */ }
        finally
        {
            clients.TryRemove(clientId, out _);
            sessions.TryRemove(clientId, out _);
            client.Dispose();
        }
    }

    private static async Task<string?> ReadBoundedLine(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (result.Length < 65536)
        {
            var count = await reader.ReadAsync(character.AsMemory(), token);
            if (count == 0) return null;
            if (character[0] == '\n') return result.ToString();
            if (character[0] != '\r') result.Append(character[0]);
        }
        throw new IOException("蓝牙请求超过 64 KiB");
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener?.Dispose();
        foreach (var client in clients.Values) client.Dispose();
        if (acceptTask != null) await acceptTask;
        await Task.WhenAll(sessions.Values);
        cancellation.Dispose();
        Status = "蓝牙服务已停止";
    }
}
