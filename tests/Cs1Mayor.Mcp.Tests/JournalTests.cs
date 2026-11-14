using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Cs1Mayor.Mcp.Tests;

public sealed class JournalTests : IDisposable
{
    private const string Summary =
        "{\"ok\":true,\"gameTime\":\"2029-11-19T15:18:01\",\"simulation\":{\"paused\":false},\"population\":144,\"cash\":655752}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cs1-journal-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 1, 2, 3, TimeSpan.FromHours(7));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static BridgeClient Bridge(StubHandler handler) => new("http://127.0.0.1:32123", 5, new HttpClient(handler));

    private Journal NewJournal(BridgeClient? bridge = null, ObsClient? obs = null)
        => new(_dir, bridge, obs, () => Start, TimeSpan.FromSeconds(2));

    private static Dictionary<string, JsonElement> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task Write_AppendsOneJsonLinePerEntry_WithGameContext()
    {
        var journal = NewJournal(Bridge(StubHandler.Json(Summary)));

        await journal.WriteAsync("decision", "  Water first: the houses have no water.  ");
        await journal.WriteAsync("plan", "Zone two streets next.");

        Assert.Equal(Path.Combine(_dir, "20261115-010203.jsonl"), journal.FilePath);
        string[] lines = File.ReadAllLines(journal.FilePath);
        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0])!;
        Assert.Equal(1, (int?)first["seq"]);
        Assert.Equal("2026-11-15T01:02:03.000+07:00", (string?)first["wall"]);
        Assert.Equal("decision", (string?)first["kind"]);
        Assert.Equal("Water first: the houses have no water.", (string?)first["text"]);
        Assert.Equal("2029-11-19T15:18:01", (string?)first["game"]!["date"]);
        Assert.Equal(144, (int?)first["game"]!["population"]);
        Assert.Equal(655752, (long?)first["game"]!["cash"]);
        Assert.Null(first["recording"]);
        Assert.Equal(2, (int?)JsonNode.Parse(lines[1])!["seq"]);
    }

    [Fact]
    public async Task Write_SurvivesAClosedGame()
    {
        var down = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var journal = NewJournal(Bridge(down));

        var entry = await journal.WriteAsync("note", "The game is closed.");

        Assert.Null(entry["game"]);
        Assert.Single(File.ReadAllLines(journal.FilePath));
    }

    [Fact]
    public async Task Write_IgnoresTheBridgeWhenNoCityIsLoaded()
    {
        var journal = NewJournal(Bridge(StubHandler.Json("{\"ok\":false,\"error\":\"no city\"}", HttpStatusCode.Conflict)));

        var entry = await journal.WriteAsync("note", "Main menu.");

        Assert.Null(entry["game"]);
    }

    [Fact]
    public async Task Write_CarriesTheRecordingTimecode()
    {
        await using var server = new StubObsServer("secret");
        var fake = new FakeObs { Recording = true };
        server.Handler = fake.Handle;
        await using var obs = new ObsClient(server.Url, "secret");
        var journal = NewJournal(obs: obs);

        var entry = await journal.WriteAsync("milestone", "First 100 citizens.");

        Assert.True((bool?)entry["recording"]!["active"]);
        Assert.Equal("00:01:02.500", (string?)entry["recording"]!["timecode"]);
    }

    [Fact]
    public async Task Write_WithObsDown_HasNoRecordingContext()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var obs = new ObsClient($"ws://127.0.0.1:{port}", "x");
        var journal = NewJournal(obs: obs);

        var entry = await journal.WriteAsync("note", "No OBS.");

        Assert.Null(entry["recording"]);
    }

    [Theory]
    [InlineData("city_build_road", "{\"startX\":1}", true)]
    [InlineData("city_build_road", "{\"startX\":1,\"dryRun\":true}", false)]
    [InlineData("city_place_building", "{\"prefab\":\"Wind Turbine\",\"dryRun\":false}", true)]
    [InlineData("city_set_tax_rate", "{\"rate\":9}", true)]
    [InlineData("studio_marker", "{\"name\":\"Day 1\"}", true)]
    [InlineData("city_overview", "{}", false)]
    [InlineData("studio_status", "{}", false)]
    [InlineData("city_request", "{\"path\":\"/state/summary\"}", false)]
    [InlineData("city_request", "{\"path\":\"/commands/save\",\"method\":\"post\"}", true)]
    public void ShouldJournal_MutatingCallsOnly(string tool, string args, bool expected)
    {
        Assert.Equal(expected, Journal.ShouldJournal(tool, Args(args)));
    }

    [Fact]
    public async Task ToolCall_RecordsToolArgsAndResult()
    {
        var journal = NewJournal();
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\"ok\":true,\"segmentIds\":[11,12],\"cost\":4800}" }],
        };

        var entry = await journal.WriteToolCallAsync("city_build_road",
            Args("{\"startX\":10,\"startZ\":20,\"endX\":330,\"endZ\":20,\"prefab\":\"Basic Road\"}"), result);

        Assert.Equal("build", (string?)entry["kind"]);
        Assert.Equal("city_build_road", (string?)entry["tool"]);
        Assert.Equal(330, (int?)entry["args"]!["endX"]);
        Assert.Equal(4800, (int?)entry["result"]!["cost"]);
        Assert.Null(entry["text"]);
    }

    [Fact]
    public async Task ToolCall_MarkerUsesItsNameAsText()
    {
        var journal = NewJournal();
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] };

        var entry = await journal.WriteToolCallAsync("studio_marker", Args("{\"name\":\"Water tower online\"}"), result);

        Assert.Equal("marker", (string?)entry["kind"]);
        Assert.Equal("Water tower online", (string?)entry["text"]);
    }

    [Fact]
    public void Summarise_PlainTextError()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }] };

        var summary = Journal.Summarise(result)!;

        Assert.False((bool?)summary["ok"]);
        Assert.Equal("boom", (string?)summary["error"]);
    }

    [Fact]
    public void Summarise_KeepsKeyFieldsOfLargeResults()
    {
        var big = new JsonObject
        {
            ["ok"] = false,
            ["errors"] = new JsonArray("ObjectCollision"),
            ["hint"] = "move it",
            ["noise"] = new string('x', 5000),
        };
        var result = new CallToolResult { Content = [new TextContentBlock { Text = big.ToJsonString() }] };

        var summary = Journal.Summarise(result)!;

        Assert.True((bool?)summary["truncated"]);
        Assert.Equal("ObjectCollision", (string?)summary["errors"]![0]);
        Assert.Null(summary["noise"]);
    }

    [Fact]
    public async Task ReadRecent_ReturnsTheNewestEntries()
    {
        var journal = NewJournal();
        for (int i = 1; i <= 5; i++) await journal.WriteAsync("note", $"n{i}");

        var recent = await journal.ReadRecentAsync(2);

        Assert.Equal(["n4", "n5"], recent.Select(e => (string)e!["text"]!).ToArray());
    }

    [Fact]
    public async Task ReadRecent_BeforeAnyEntry_IsEmpty()
    {
        Assert.Empty(await NewJournal().ReadRecentAsync(10));
    }

    [Fact]
    public async Task NoteTool_ValidatesKind()
    {
        var tools = new JournalTools(NewJournal());

        var bad = await tools.StudioNote("text", "rant");
        var good = await tools.StudioNote("Schools before offices.", "Decision");

        Assert.False((bool?)bad!["ok"]);
        Assert.True((bool?)good!["ok"]);
        Assert.Equal("decision", (string?)good["entry"]!["kind"]);
    }

    [Fact]
    public async Task JournalTool_ReadsBack()
    {
        var journal = NewJournal();
        var tools = new JournalTools(journal);
        await tools.StudioNote("A", "plan");

        var read = await tools.StudioJournal(10);

        Assert.Equal("20261115-010203", (string?)read!["session"]);
        Assert.Single(read["entries"]!.AsArray());
    }
}
