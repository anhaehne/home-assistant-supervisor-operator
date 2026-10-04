using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace CoreGateway;

public interface ICoreHttpConfigurationTransport
{
    Task<JsonObject> Read(CancellationToken cancellation);
    Task Configure(JsonObject configuration, CancellationToken cancellation);
    Task Promote(CancellationToken cancellation);
}

// The privileged socket is never exposed as a caller-selected URL or command.
public sealed class CoreHttpConfigurationTransport(string socketPath) : ICoreHttpConfigurationTransport
{
    public async Task<JsonObject> Read(CancellationToken cancellation) =>
        await Command("http/config", null, cancellation) as JsonObject ?? throw new InvalidDataException("Invalid native HTTP configuration response.");
    public async Task Configure(JsonObject configuration, CancellationToken cancellation) =>
        _ = await Command("http/config/configure", configuration, cancellation);
    public async Task Promote(CancellationToken cancellation) =>
        _ = await Command("http/config/promote", null, cancellation);

    private async Task<JsonNode?> Command(string command, JsonObject? configuration, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var websocket = new ClientWebSocket();
        await websocket.ConnectAsync(new Uri("ws://localhost/api/websocket"), invoker, deadline.Token);
        if ((await Receive(websocket, deadline.Token))["type"]?.GetValue<string>() != "auth_ok")
            throw new InvalidDataException("Core did not authenticate the dedicated Supervisor socket.");
        var request = new JsonObject { ["id"] = 1, ["type"] = command };
        if (configuration is not null) request["config"] = configuration.DeepClone();
        await websocket.SendAsync(System.Text.Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token);
        var response = await Receive(websocket, deadline.Token);
        if (response["type"]?.GetValue<string>() != "result" || response["id"]?.GetValue<int>() != 1 || response["success"]?.GetValue<bool>() != true)
            throw new InvalidDataException("Core rejected the native HTTP configuration request.");
        return response["result"]?.DeepClone();
    }

    private static async Task<JsonObject> Receive(ClientWebSocket websocket, CancellationToken cancellation)
    {
        var buffer = new byte[4096];
        using var output = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (received.MessageType != WebSocketMessageType.Text || output.Length + received.Count > 65536)
                throw new InvalidDataException("Invalid native WebSocket response.");
            output.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return JsonNode.Parse(output.ToArray()) as JsonObject ?? throw new InvalidDataException("Invalid native WebSocket response.");
    }
}
