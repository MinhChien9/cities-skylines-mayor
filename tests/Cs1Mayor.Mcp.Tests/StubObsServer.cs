using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp.Tests;

/// <summary>What the stub answers to one request.</summary>
public sealed record StubResponse(bool Ok, int Code = 100, string? Comment = null, JsonNode? Data = null)
{
    public static StubResponse Success(JsonNode? data = null) => new(true, 100, null, data);
}

/// <summary>
/// A tiny obs-websocket v5 server for tests: a raw TCP listener doing the HTTP upgrade by
/// hand (no ASP.NET dependency), Hello with a salt/challenge, Identify checked with the
/// real formula (close 4009 on a wrong password), and requests answered by
/// <see cref="Handler"/>. It can drop every connection and push events.
/// </summary>
public sealed class StubObsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _password;
    private readonly ConcurrentDictionary<WebSocket, (TcpClient Tcp, SemaphoreSlim SendLock)> _clients = new();
    private readonly Task _acceptLoop;
    private int _connections;
    private int _disposed;

    public StubObsServer(string? password = "secret")
    {
        _password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public Func<string, JsonObject?, Task<StubResponse>> Handler { get; set; } =
        (_, _) => Task.FromResult(StubResponse.Success());

    /// <summary>Connections that completed Identify.</summary>
    public int Connections => Volatile.Read(ref _connections);

    public ConcurrentQueue<string> Requests { get; } = new();

    public int LastEventSubscriptions { get; private set; }

    /// <summary>Kills every connection without a close handshake, like OBS crashing.</summary>
    public void DropAll()
    {
        foreach (var (socket, (tcp, _)) in _clients)
        {
            socket.Abort();
            tcp.Close();
        }
        _clients.Clear();
    }

    public async Task SendEventAsync(string eventType, JsonObject? eventData = null)
    {
        foreach (var (socket, (_, sendLock)) in _clients)
        {
            var d = new JsonObject { ["eventType"] = eventType, ["eventIntent"] = 64 };
            if (eventData is not null) d["eventData"] = eventData.DeepClone();
            await SendAsync(socket, sendLock, new JsonObject { ["op"] = 5, ["d"] = d });
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        WebSocket? socket = null;
        try
        {
            var stream = tcp.GetStream();
            string key = await ReadUpgradeKeyAsync(stream);
            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(head, _cts.Token);

            socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            var sendLock = new SemaphoreSlim(1, 1);

            string salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var hello = new JsonObject { ["obsWebSocketVersion"] = "5.7.6", ["rpcVersion"] = 1 };
            if (_password is not null)
            {
                hello["authentication"] = new JsonObject { ["challenge"] = challenge, ["salt"] = salt };
            }
            await SendAsync(socket, sendLock, new JsonObject { ["op"] = 0, ["d"] = hello });

            var identify = await ReceiveAsync(socket);
            if (identify is null || (int?)identify["op"] != 1) return;
            if (_password is not null)
            {
                string expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
                    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_password + salt))) + challenge)));
                if ((string?)identify["d"]?["authentication"] != expected)
                {
                    await socket.CloseOutputAsync((WebSocketCloseStatus)4009, "Authentication failed.", _cts.Token);
                    return;
                }
            }
            LastEventSubscriptions = (int?)identify["d"]?["eventSubscriptions"] ?? 0;
            await SendAsync(socket, sendLock, new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
            _clients[socket] = (tcp, sendLock);
            Interlocked.Increment(ref _connections);

            while (true)
            {
                var message = await ReceiveAsync(socket);
                if (message is null) return;
                if ((int?)message["op"] != 6) continue;
                var d = (JsonObject)message["d"]!;
                string type = (string)d["requestType"]!;
                string id = (string)d["requestId"]!;
                Requests.Enqueue(type);
                var data = d["requestData"] as JsonObject;
                _ = Task.Run(async () =>
                {
                    var response = await Handler(type, data);
                    var status = new JsonObject { ["result"] = response.Ok, ["code"] = response.Code };
                    if (response.Comment is not null) status["comment"] = response.Comment;
                    var reply = new JsonObject
                    {
                        ["requestType"] = type, ["requestId"] = id, ["requestStatus"] = status,
                    };
                    if (response.Data is not null) reply["responseData"] = response.Data.DeepClone();
                    try { await SendAsync(socket, sendLock, new JsonObject { ["op"] = 7, ["d"] = reply }); }
                    catch { /* dropped */ }
                });
            }
        }
        catch
        {
            // Dropped connections end here.
        }
        finally
        {
            if (socket is not null) _clients.TryRemove(socket, out _);
        }
    }

    private static async Task<string> ReadUpgradeKeyAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("closed during upgrade");
            bytes.Add(one[0]);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
        }
        foreach (string line in Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Sec-WebSocket-Key:".Length..].Trim();
            }
        }
        throw new IOException("no Sec-WebSocket-Key");
    }

    private async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, JsonObject message)
    {
        await sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task<JsonObject?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(stream.ToArray()) as JsonObject;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        DropAll();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
