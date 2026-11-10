using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Thin HTTP client for the in-game CS1 Mayor Bridge mod.
///
/// The bridge listens on 127.0.0.1:32123. Every request it serves is executed on the
/// game's *simulation* thread, so the client never has to think about game threading.
///
/// Transport note: the bridge uses plain HTTP over loopback rather than a raw socket
/// protocol. Measured latency of the HTTP path inside Unity's Mono is ~139 us p50, while
/// the agent's own reasoning latency is 2-60 SECONDS per decision. The transport accounts
/// for roughly 0.0005% of a decision cycle, so the readability and debuggability of HTTP
/// (curl, Invoke-RestMethod, packet captures) is worth far more than the microseconds.
/// </summary>
public sealed class BridgeClient
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    public BridgeClient(string? baseUrl = null, int timeoutSeconds = 30)
    {
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("CS1_BRIDGE_URL")
                    ?? "http://127.0.0.1:32123").TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
    }

    public string BaseUrl => _baseUrl;

    /// <summary>
    /// Performs a GET against the bridge and returns the parsed JSON body.
    /// Throws <see cref="BridgeException"/> when the bridge is unreachable or returns an error.
    /// </summary>
    public async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        return await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a POST with a JSON body against the bridge and returns the parsed JSON body.
    /// </summary>
    public async Task<JsonNode?> PostAsync(string path, object? body, CancellationToken cancellationToken = default)
    {
        return await SendAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when the bridge answers /health. Used to give the agent a clear
    /// "the game is not running" message instead of a raw socket error.
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await GetAsync("/health", cancellationToken).ConfigureAwait(false);
            return health?["ok"]?.GetValue<bool>() ?? false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body != null)
        {
            string json = JsonSerializer.Serialize(body, _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new BridgeException(
                $"Could not reach the CS1 Mayor Bridge at {_baseUrl}. Is Cities: Skylines running with the mod enabled? ({ex.Message})",
                ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new BridgeException(
                $"The CS1 Mayor Bridge at {_baseUrl} did not respond in time. The game may be loading a map or busy simulating. ({ex.Message})",
                ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new BridgeException(
                    $"Bridge request {method} {path} failed with HTTP {(int)response.StatusCode}: {Summarize(text)}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new BridgeException($"Bridge returned a non-JSON response: {Summarize(text)}", ex);
            }
        }
    }

    /// <summary>
    /// Truncates a body so an error message stays readable inside an agent's context window.
    /// </summary>
    private static string Summarize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty body)";
        }

        string trimmed = text.Trim();
        const int max = 600;
        return trimmed.Length <= max ? trimmed : trimmed[..max] + " ...(truncated)";
    }
}

/// <summary>Raised when the in-game bridge cannot be reached or reports a failure.</summary>
public sealed class BridgeException : Exception
{
    public BridgeException(string message) : base(message)
    {
    }

    public BridgeException(string message, Exception inner) : base(message, inner)
    {
    }
}
