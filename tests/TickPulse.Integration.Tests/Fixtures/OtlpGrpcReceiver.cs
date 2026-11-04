using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>
/// A working OTLP/gRPC collector, as far as an exporter can tell: HTTP/2 without TLS on loopback, every call answered
/// with an empty response message and <c>grpc-status: 0</c>. It records each call's path and raw request body (the
/// 5-byte gRPC frame header followed by the protobuf message), so tests can look for the bytes they expect.
/// </summary>
public sealed class OtlpGrpcReceiver : IAsyncDisposable
{
    public const string LogsExportPath = "/opentelemetry.proto.collector.logs.v1.LogsService/Export";

    private readonly ConcurrentQueue<ReceivedCall> _calls = new();
    private WebApplication? _app;

    public Uri Endpoint { get; private set; } = null!;

    public IReadOnlyCollection<ReceivedCall> Calls => [.. _calls];

    public static async Task<OtlpGrpcReceiver> StartAsync()
    {
        var receiver = new OtlpGrpcReceiver();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.Run(receiver.HandleAsync);
        await app.StartAsync();
        receiver._app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        receiver.Endpoint = new Uri(address);
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        _calls.Enqueue(new ReceivedCall(context.Request.Path, body.ToArray()));

        context.Response.ContentType = "application/grpc";
        context.Response.DeclareTrailer("grpc-status");
        // One uncompressed frame holding an empty Export*ServiceResponse.
        await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    public sealed record ReceivedCall(string Path, byte[] Body)
    {
        public bool Contains(ReadOnlySpan<byte> bytes) => Body.AsSpan().IndexOf(bytes) >= 0;
    }
}
