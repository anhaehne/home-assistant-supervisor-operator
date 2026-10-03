using System.Net.Sockets;

namespace CoreGateway;

public sealed class CoreSocketClient : IDisposable
{
    private readonly HttpClient client;

    public CoreSocketClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        };
        client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public Task<HttpResponseMessage> ReadStatus(CancellationToken cancellation) =>
        client.GetAsync("/api/", HttpCompletionOption.ResponseHeadersRead, cancellation);

    public Task<HttpResponseMessage> ReadConfiguration(CancellationToken cancellation) =>
        client.GetAsync("/api/config", HttpCompletionOption.ResponseHeadersRead, cancellation);

    public void Dispose() => client.Dispose();
}
