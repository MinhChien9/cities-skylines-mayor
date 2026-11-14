using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp;

/// <summary>
/// A minimal obs-websocket v5 client: Hello/Identify with SHA-256 authentication,
/// request/response matched by request id, events, and reconnect on the next request.
///
/// The studio tools use it to drive OBS (record, chapter markers, screenshots). It is
/// deliberately small and dependency-free; the protocol is documented at
/// https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md.
///
/// Failure semantics follow the mayor's rule "a timeout does not mean it did not
/// happen": a request whose connection drops after it was sent fails with an
/// <see cref="ObsException"/> instead of being retried, because OBS may have run it.
/// Only a request that could not be sent at all is retried once on a fresh connection.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    private const int OpHello = 0;
    private const int OpIdentify = 1;
    private const int OpIdentified = 2;
    private const int OpEvent = 5;
    private const int OpRequest = 6;
    private const int OpRequestResponse = 7;

    /// <summary>obs-websocket close code for a wrong password.</summary>
    public const int CloseAuthenticationFailed = 4009;

    /// <summary>Event subscription bit for output events (RecordStateChanged, ...).</summary>
    public const int SubscribeOutputs = 1 << 6;

    private readonly Uri _url;
    private readonly string? _password;
    private readonly int _eventSubscriptions;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private long _nextRequestId;
    private int _connectionCount;

    public ObsClient(string url, string? password, int eventSubscriptions = SubscribeOutputs,
        TimeSpan? connectTimeout = null, TimeSpan? requestTimeout = null)
    {
        _url = new Uri(url);
        _password = string.IsNullOrEmpty(password) ? null : password;
        _eventSubscriptions = eventSubscriptions;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Raised on the receive loop for every OBS event: (eventType, eventData).</summary>
    public event Action<string, JsonObject?>? EventReceived;

    public Uri Url => _url;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>How many times a connection was established (1 + reconnects).</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>The obs-websocket version announced in the last Hello.</summary>
    public string? WebSocketVersion { get; private set; }

    /// <summary>
    /// Sends one request and returns its <c>responseData</c> (null when OBS returns none).
    /// Throws <see cref="ObsException"/> when OBS is unreachable, refuses the request, or
    /// the connection drops before the answer arrives.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(string requestType, JsonObject? requestData = null,
        CancellationToken cancellationToken = default)
    {
        string id = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var d = new JsonObject { ["requestType"] = requestType, ["requestId"] = id };
        if (requestData is not null)
        {
            d["requestData"] = requestData.DeepClone();
        }
        var message = new JsonObject { ["op"] = OpRequest, ["d"] = d };

        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await SendWithOneReconnectAsync(id, answer, message, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            JsonObject response;
            try
            {
                response = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ObsException(
                    $"OBS did not answer {requestType} within {_requestTimeout.TotalSeconds:0} s. " +
                    "It may still have run; read the state before retrying.");
            }

            var status = response["requestStatus"] as JsonObject;
            bool ok = status?["result"] is JsonValue r && r.TryGetValue(out bool b) && b;
            if (!ok)
            {
                int? code = status?["code"] is JsonValue c && c.TryGetValue(out int ci) ? ci : null;
                string? comment = status?["comment"]?.GetValue<string>();
                throw new ObsException(
                    $"OBS refused {requestType} (code {code?.ToString(CultureInfo.InvariantCulture) ?? "?"})" +
                    (string.IsNullOrEmpty(comment) ? "." : $": {comment}"), code);
            }
            return response["responseData"]?.DeepClone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Connects now (otherwise the first request connects).</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The obs-websocket authentication string:
    /// base64(sha256(base64(sha256(password + salt)) + challenge)).
    /// </summary>
    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task SendWithOneReconnectAsync(string id, TaskCompletionSource<JsonObject> answer,
        JsonObject message, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            // Registered with its connection, so a dying older connection cannot fail it.
            _pending[id] = new Pending(answer, socket);
            try
            {
                await SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt == 0 && ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // The frame never left: the connection was already dead. Drop it and retry once.
                await ResetAsync(socket).ConfigureAwait(false);
            }
        }
    }

    private async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var current = _socket;
        if (current is not null && current.State == WebSocketState.Open)
        {
            return current;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _socket;
            if (current is not null && current.State == WebSocketState.Open)
            {
                return current;
            }
            if (current is not null)
            {
                await ResetAsync(current).ConfigureAwait(false);
            }

            var socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            try
            {
                await socket.ConnectAsync(_url, timeout.Token).ConfigureAwait(false);
                await HandshakeAsync(socket, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObsException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new ObsException(
                    $"OBS is not reachable at {_url} ({ex.Message}). Is OBS running with obs-websocket " +
                    "enabled? Run scripts/setup-obs.ps1.", null, ex);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            Interlocked.Increment(ref _connectionCount);
            _receiveCts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token));
            return socket;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task HandshakeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var hello = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (hello["op"]?.GetValue<int>() != OpHello)
        {
            throw new ObsException("OBS did not start with Hello; is this an obs-websocket v5 server?");
        }
        var helloData = hello["d"] as JsonObject;
        WebSocketVersion = helloData?["obsWebSocketVersion"]?.GetValue<string>();

        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = _eventSubscriptions };
        if (helloData?["authentication"] is JsonObject auth)
        {
            if (_password is null)
            {
                throw new ObsException("OBS asks for a password but none is configured (studio.local.json).");
            }
            identify["authentication"] = ComputeAuthentication(_password,
                auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        await SendAsync(socket, new JsonObject { ["op"] = OpIdentify, ["d"] = identify }, cancellationToken)
            .ConfigureAwait(false);

        JsonObject identified;
        try
        {
            identified = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        catch (ObsException ex) when (ex.Code == CloseAuthenticationFailed)
        {
            throw new ObsException(
                "OBS rejected the password. Re-run scripts/setup-obs.ps1 so studio.local.json and OBS agree.",
                ex.Code, ex);
        }
        if (identified["op"]?.GetValue<int>() != OpIdentified)
        {
            throw new ObsException($"OBS did not confirm Identify (op {identified["op"]}).");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        Exception? reason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                int op = message["op"]?.GetValue<int>() ?? -1;
                var d = message["d"] as JsonObject;
                if (op == OpRequestResponse && d?["requestId"]?.GetValue<string>() is { } id
                    && _pending.TryGetValue(id, out var pending))
                {
                    pending.Answer.TrySetResult(d);
                }
                else if (op == OpEvent && d?["eventType"]?.GetValue<string>() is { } eventType)
                {
                    try
                    {
                        EventReceived?.Invoke(eventType, d["eventData"] as JsonObject);
                    }
                    catch
                    {
                        // A faulty event handler must not take the connection down.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            reason = ex;
        }

        // Anything still waiting on this connection will never get its answer.
        var lost = new ObsException(
            "The connection to OBS was lost before it answered. The request may or may not have run; " +
            "read the state before retrying." + (reason is null ? "" : $" ({reason.Message})"), null, reason);
        foreach (var pending in _pending.Values)
        {
            if (ReferenceEquals(pending.Socket, socket))
            {
                pending.Answer.TrySetException(lost);
            }
        }
    }

    private async Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                int? code = (int?)result.CloseStatus;
                throw new ObsException(
                    $"OBS closed the connection ({code?.ToString(CultureInfo.InvariantCulture) ?? "?"}" +
                    $"{(string.IsNullOrEmpty(result.CloseStatusDescription) ? "" : ": " + result.CloseStatusDescription)}).",
                    code);
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        return JsonNode.Parse(stream.ToArray()) as JsonObject
               ?? throw new ObsException("OBS sent a message that is not a JSON object.");
    }

    private async Task ResetAsync(ClientWebSocket socket)
    {
        Task? loop = null;
        if (ReferenceEquals(_socket, socket))
        {
            _receiveCts?.Cancel();
            _socket = null;
            loop = _receiveLoop;
            _receiveLoop = null;
        }
        socket.Abort();
        socket.Dispose();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* already reported to pending requests */ }
        }
    }

    private sealed record Pending(TaskCompletionSource<JsonObject> Answer, ClientWebSocket Socket);

    public async ValueTask DisposeAsync()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }
        _receiveCts?.Cancel();
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Closing is best effort.
            }
        }
        await ResetAsync(socket).ConfigureAwait(false);
    }
}

/// <summary>An OBS request failed, OBS is unreachable, or the connection dropped.</summary>
public sealed class ObsException : Exception
{
    public ObsException(string message, int? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>The obs-websocket request status code or close code, when there is one.</summary>
    public int? Code { get; }
}
