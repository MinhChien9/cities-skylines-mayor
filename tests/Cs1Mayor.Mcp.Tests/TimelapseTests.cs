using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp.Tests;

public sealed class TimelapseTests : IAsyncLifetime
{
    private const string CameraState =
        "{\"ok\":true,\"camera\":{\"target\":{\"x\":1,\"y\":2,\"z\":3},\"angle\":{\"vertical\":50,\"horizontal\":20},\"distance\":900,\"uiHidden\":false}}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs1-timelapse-{Guid.NewGuid():N}");
    private readonly StubObsServer _server = new("secret");
    private readonly FakeObs _obs = new();
    private DateTime _gameTime = new(2029, 11, 19, 12, 0, 0);
    private StubHandler _bridgeHandler = null!;
    private ObsClient _client = null!;
    private ViewpointStore _viewpoints = null!;
    private TimelapseRunner _runner = null!;

    public Task InitializeAsync()
    {
        _server.Handler = _obs.Handle;
        _client = new ObsClient(_server.Url, "secret", requestTimeout: TimeSpan.FromSeconds(3));
        _bridgeHandler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                request.RequestUri!.AbsolutePath == "/state/summary"
                    ? $"{{\"ok\":true,\"gameTime\":\"{_gameTime:yyyy-MM-ddTHH:mm:ss}\"}}"
                    : CameraState,
                Encoding.UTF8, "application/json"),
        });
        var bridge = new BridgeClient("http://127.0.0.1:32123", 5, new HttpClient(_bridgeHandler));
        _viewpoints = new ViewpointStore(Path.Combine(_root, "viewpoints.json"));
        _viewpoints.Save("overview", new CameraFrame(330, 0, -640, 1200, 60, 0));
        _viewpoints.Save("street", new CameraFrame(300, 0, -600, 300, 20, 90));
        _runner = new TimelapseRunner(bridge, _client, new StudioSettings { RecordDirectory = _root }, _viewpoints,
            pollInterval: TimeSpan.FromMilliseconds(30), settle: TimeSpan.Zero, recordingGuard: TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runner.DisposeAsync();
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private List<JsonObject> Saves() => _obs.Screenshots.Where(s => (string?)s?["imageFormat"] == "png").Select(s => s!).ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [Theory]
    [InlineData(null, 7, true)]
    [InlineData(0.0, 7, false)]
    [InlineData(6.9, 7, false)]
    [InlineData(7.0, 7, true)]
    public void IsDue(double? daysSince, double everyDays, bool expected)
    {
        var now = new DateTime(2030, 1, 10);
        DateTime? last = daysSince is null ? null : now.AddDays(-daysSince.Value);
        Assert.Equal(expected, TimelapseRunner.IsDue(last, now, everyDays));
    }

    [Fact]
    public async Task Start_CapturesAtOnce_ThenEveryNGameDays()
    {
        var started = _runner.Start(["overview", "street"], 7);
        Assert.True((bool?)started["ok"]);

        await WaitUntil(() => Saves().Count == 2);
        await Task.Delay(150);
        Assert.Equal(2, Saves().Count);             // game time has not moved

        _gameTime = _gameTime.AddDays(7);
        await WaitUntil(() => Saves().Count == 4);
        var stopped = await _runner.StopAsync();

        Assert.Equal(2, (int?)stopped["captures"]);
        Assert.Equal(4, (int?)stopped["frames"]);
        string folder = (string)stopped["folder"]!;
        var paths = Saves().Select(s => (string)s["imageFilePath"]!).ToList();
        Assert.Equal(Path.Combine(folder, "overview", "0001_2029-11-19.png"), paths[0]);
        Assert.Equal(Path.Combine(folder, "street", "0001_2029-11-19.png"), paths[1]);
        Assert.Equal(Path.Combine(folder, "overview", "0002_2029-11-26.png"), paths[2]);
        Assert.Equal(4, File.ReadAllLines(Path.Combine(folder, "frames.jsonl")).Length);
    }

    [Fact]
    public async Task Capture_HidesUi_RestoresCamera_AndPausesTheRecording()
    {
        _obs.Recording = true;

        var result = await _runner.CaptureNowAsync(["overview"], CancellationToken.None);

        Assert.True((bool?)result["ok"]);
        var cameraBodies = _bridgeHandler.Requests.Where(r => r.Path == "/commands/set-camera")
            .Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal(3, cameraBodies.Count);                       // read, viewpoint, restore
        Assert.True((bool?)cameraBodies[1]["hideUi"]);
        Assert.Equal(1200, (double?)cameraBodies[1]["distance"]);
        Assert.Equal(900, (double?)cameraBodies[2]["distance"]);    // put back
        Assert.False((bool?)cameraBodies[2]["hideUi"]);
        var order = _server.Requests.ToList();
        Assert.True(order.IndexOf("PauseRecord") < order.IndexOf("SaveSourceScreenshot"));
        Assert.True(order.IndexOf("SaveSourceScreenshot") < order.IndexOf("ResumeRecord"));
        Assert.False(_obs.Paused);
    }

    [Fact]
    public async Task Capture_LeavesAPausedRecordingAlone()
    {
        _obs.Recording = true;
        _obs.Paused = true;

        await _runner.CaptureNowAsync(["overview"], CancellationToken.None);

        Assert.DoesNotContain("PauseRecord", _server.Requests);
        Assert.DoesNotContain("ResumeRecord", _server.Requests);
    }

    [Fact]
    public void Start_RejectsUnknownViewpoints()
    {
        var result = _runner.Start(["overview", "nope"], 7);

        Assert.False((bool?)result["ok"]);
        Assert.Contains("nope", (string?)result["error"]);
        Assert.False(_runner.IsRunning);
    }

    [Fact]
    public async Task Start_Twice_IsRefused()
    {
        _runner.Start(["overview"], 7);

        var again = _runner.Start(["overview"], 7);

        Assert.False((bool?)again["ok"]);
        await _runner.StopAsync();
    }

    [Fact]
    public async Task Stop_WhenIdle_IsAnError()
    {
        Assert.False((bool?)(await _runner.StopAsync())["ok"]);
    }
}
