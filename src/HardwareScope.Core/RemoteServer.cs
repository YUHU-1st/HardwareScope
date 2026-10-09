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
    }

    public async ValueTask DisposeAsync()
    {
        if (application == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await application.StopAsync(timeout.Token);
        await application.DisposeAsync();
        application = null;
    }
}
