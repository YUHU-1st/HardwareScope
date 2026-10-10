using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HardwareScope.Core;

internal sealed class LanDiscovery(int port) : IAsyncDisposable
{
    public const int DiscoveryPort = 47832;
    private static readonly byte[] Request = Encoding.UTF8.GetBytes("HardwareScope.Discover.v1");
    private readonly CancellationTokenSource cancellation = new();
    private UdpClient? client;
    private Task? listening;

    public void Start()
    {
        client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            // A second monitor may use another HTTPS port. One discovery listener is sufficient.
            client.Client.ExclusiveAddressUse = true;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            listening = Listen();
        }
        catch (SocketException)
        {
            client.Dispose();
            client = null;
        }
    }

    private async Task Listen()
    {
        var response = JsonSerializer.SerializeToUtf8Bytes(new { product = "HardwareScope", name = Environment.MachineName, port }, SettingsStore.Json);
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var received = await client!.ReceiveAsync(cancellation.Token);
                if (!received.Buffer.AsSpan().SequenceEqual(Request) || !LocalNetwork.IsLocalPeer(received.RemoteEndPoint.Address)) continue;
                await client.SendAsync(response, received.RemoteEndPoint, cancellation.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested) break;
                // A UDP sender can disappear before its reply. Keep accepting discovery requests.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        client?.Dispose();
        if (listening != null) await listening;
        cancellation.Dispose();
    }
}
