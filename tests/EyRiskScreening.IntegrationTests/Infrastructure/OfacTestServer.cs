using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal sealed class OfacTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentDictionary<string, int> _requestCounts =
        new(StringComparer.OrdinalIgnoreCase);

    private OfacTestServer(WebApplication application)
    {
        _application = application;
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        BaseAddress = new Uri((addresses ?? []).Single());
    }

    public Uri BaseAddress { get; }

    public int RequestCount(string path) =>
        _requestCounts.TryGetValue(path, out var count) ? count : 0;

    public static async Task<OfacTestServer> StartAsync(
        Func<HttpContext, Task> handler,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        OfacTestServer? server = null;
        application.Run(async context =>
        {
            _ = server!._requestCounts.AddOrUpdate(
                context.Request.Path.Value ?? string.Empty,
                1,
                static (_, count) => count + 1);
            await handler(context);
        });
        await application.StartAsync(cancellationToken);
        server = new OfacTestServer(application);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}
