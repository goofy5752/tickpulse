using System.Net;
using System.Net.Sockets;

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>
/// Accepts TCP connections on loopback and never sends a byte back: a collector or database that is up at the network
/// level but does not answer.
/// </summary>
public sealed class SilentTcpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();

    public SilentTcpServer()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Endpoint => new($"http://127.0.0.1:{Port}");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_clients)
        {
            _clients.ForEach(c => c.Dispose());
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_clients)
                {
                    _clients.Add(client);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }
}
