using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HardwareScope.Core;

internal sealed class RemoteServer(MonitorHost host, X509Certificate2 certificate) : IAsyncDisposable
{
    private WebApplication? application;
    private LanDiscovery? discovery;
    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 65536;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.ListenAnyIP(port, listen => listen.UseHttps(certificate));
        });
        application = builder.Build();
        application.MapGet("/health", () => Results.Json(new { status = "ok", product = "HardwareScope" }));
        application.Map("/pair/{**path}", async context =>
        {
            if (!LocalNetwork.IsLocalPeer(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new CommandResult(false, "配对仅允许同一局域网内的设备"), SettingsStore.Json);
                return;
            }
            if (context.Request.Method != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.Headers.Allow = "POST";
                await context.Response.WriteAsJsonAsync(new CommandResult(false, "配对接口仅接受 POST 请求"), SettingsStore.Json);
                return;
            }
            try
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var result = await host.DispatchAsync(context.Request.Method, context.Request.Path.Value!, json.RootElement);
                context.Response.StatusCode = result.Status;
                await context.Response.WriteAsJsonAsync(result.Body, result.Body.GetType(), SettingsStore.Json, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new CommandResult(false, "配对请求格式无效"), SettingsStore.Json);
            }
        });
        application.Map("/api/{**path}", async context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !host.Authorize(authorization[7..]))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new CommandResult(false, "授权密钥无效"), SettingsStore.Json);
                return;
            }
            try
            {
                JsonElement? body = null;
                if (context.Request.Method is "PUT" or "POST")
                {
                    using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    body = json.RootElement.Clone();
                }
                var result = await host.DispatchAsync(context.Request.Method, context.Request.Path.Value!, body);
                context.Response.StatusCode = result.Status;
                await context.Response.WriteAsJsonAsync(result.Body, result.Body.GetType(), SettingsStore.Json, cancellationToken: context.RequestAborted);
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new CommandResult(false, e.Message), SettingsStore.Json);
            }
        });
        await application.StartAsync();
        discovery = new LanDiscovery(port);
        discovery.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (discovery != null) { await discovery.DisposeAsync(); discovery = null; }
        if (application == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await application.StopAsync(timeout.Token);
        await application.DisposeAsync();
        application = null;
    }
}
