using System.Net;
using System.Text;
using Cs1Mayor.Mcp;

namespace Cs1Mayor.Mcp.Tests;

/// <summary>
/// A stub HTTP handler that answers with canned responses, so the bridge client can be
/// tested without the game running.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<(string Method, string Path, string? Body)> Requests { get; } = new();

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));

        return _responder(request);
    }
}

public class BridgeClientTests
{
    private static BridgeClient Client(StubHandler handler, string baseUrl = "http://127.0.0.1:32123")
    {
        return new BridgeClient(baseUrl, 5, new HttpClient(handler));
    }

    [Fact]
    public async Task GetAsync_returns_the_parsed_json_body()
    {
        var client = Client(StubHandler.Json("{\"ok\":true,\"population\":1234}"));

        var result = await client.GetAsync("/state/summary");

        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal(1234, result["population"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetAsync_sends_the_request_to_the_configured_base_url()
    {
        var handler = StubHandler.Json("{\"ok\":true}");
        var client = Client(handler);

        await client.GetAsync("/state/problems?limit=10");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/state/problems?limit=10", request.Path);
    }

    [Fact]
    public async Task GetAsync_throws_a_bridge_exception_for_a_non_success_status()
    {
        var client = Client(StubHandler.Json("{\"ok\":false,\"error\":\"No city is loaded.\"}", HttpStatusCode.Conflict));

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.GetAsync("/state/summary"));

        Assert.Contains("409", ex.Message);
        Assert.Contains("No city is loaded.", ex.Message);
    }

    [Fact]
    public async Task GetAsync_throws_a_bridge_exception_for_a_non_json_body()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
        }));

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.GetAsync("/state/summary"));

        Assert.Contains("non-JSON", ex.Message);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_empty_body()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        }));

        Assert.Null(await client.GetAsync("/state/summary"));
    }

    [Fact]
    public async Task PostAsync_serializes_the_body_as_json()
    {
        var handler = StubHandler.Json("{\"ok\":true}");
        var client = Client(handler);

        await client.PostAsync("/commands/build-road", new { roadPrefab = "Basic Road", dryRun = true });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/commands/build-road", request.Path);
        Assert.Contains("\"roadPrefab\":\"Basic Road\"", request.Body);
        Assert.Contains("\"dryRun\":true", request.Body);
    }

    [Fact]
    public async Task IsAliveAsync_returns_false_when_the_bridge_cannot_be_reached()
    {
        var client = Client(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        Assert.False(await client.IsAliveAsync());
    }

    [Fact]
    public async Task IsAliveAsync_returns_true_when_the_bridge_reports_ok()
    {
        var client = Client(StubHandler.Json("{\"ok\":true}"));

        Assert.True(await client.IsAliveAsync());
    }

    [Fact]
    public void BaseUrl_has_no_trailing_slash()
    {
        var client = new BridgeClient("http://127.0.0.1:32123/");

        Assert.Equal("http://127.0.0.1:32123", client.BaseUrl);
    }
}
