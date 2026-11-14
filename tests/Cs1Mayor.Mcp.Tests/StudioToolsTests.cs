using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Cs1Mayor.Mcp.Tests;

/// <summary>
/// A small model of OBS's recording state behind <see cref="StubObsServer"/>, enough for
/// the studio tools: scenes, record start/stop/pause, chapters, screenshots.
/// </summary>
internal sealed class FakeObs
{
    public string Scene = "Scene";
    public bool CaptureActive = true;
    public bool Recording;
    public bool Paused;
    public readonly List<string> Chapters = new();
    public readonly List<JsonObject?> Screenshots = new();

    public Task<StubResponse> Handle(string type, JsonObject? data)
    {
        StubResponse ok(JsonObject? d = null) => StubResponse.Success(d);
        StubResponse fail(int code, string comment) => new(false, code, comment);
        var result = type switch
        {
            "GetVersion" => ok(new JsonObject { ["obsVersion"] = "33.0.1", ["obsWebSocketVersion"] = "5.7.6" }),
            "GetCurrentProgramScene" => ok(new JsonObject { ["sceneName"] = Scene }),
            "SetCurrentProgramScene" => Set(() => Scene = (string)data!["sceneName"]!),
            "GetRecordDirectory" => ok(new JsonObject { ["recordDirectory"] = @"D:\rec" }),
            "GetSourceActive" => (string?)data?["sourceName"] == "Game Capture: Cities"
                ? ok(new JsonObject { ["videoActive"] = CaptureActive, ["videoShowing"] = CaptureActive })
                : fail(600, "No source was found by the name of `x`."),
            "GetRecordStatus" => ok(new JsonObject
            {
                ["outputActive"] = Recording, ["outputPaused"] = Paused,
                ["outputTimecode"] = Recording ? "00:01:02.500" : "00:00:00.000",
                ["outputDuration"] = Recording ? 62500 : 0, ["outputBytes"] = Recording ? 1000 : 0,
            }),
            "StartRecord" => Recording ? fail(500, "Output is running") : Set(() => Recording = true),
            "StopRecord" => Recording
                ? Then(() => Recording = false, new JsonObject { ["outputPath"] = @"D:\rec\cs1-mayor 1.mp4" })
                : fail(501, "Output is not running"),
            "PauseRecord" => Recording && !Paused ? Set(() => Paused = true) : fail(502, "Output is paused"),
            "ResumeRecord" => Paused ? Set(() => Paused = false) : fail(503, "Output is not paused"),
            "CreateRecordChapter" => !Recording ? fail(501, "Output is not running")
                : Paused ? fail(702, "Verify that the output is not paused.")
                : Set(() => Chapters.Add((string)data!["chapterName"]!)),
            "GetSourceScreenshot" => Then(() => Screenshots.Add(data),
                new JsonObject { ["imageData"] = "data:image/jpg;base64," + Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }) }),
            "SaveSourceScreenshot" => Set(() => Screenshots.Add(data)),
            _ => fail(204, "unknown request"),
        };
        return Task.FromResult(result);
    }

    private static StubResponse Set(Action action) => Then(action, null);

    private static StubResponse Then(Action action, JsonObject? data)
    {
        action();
        return StubResponse.Success(data);
    }
}

public sealed class StudioToolsTests : IAsyncLifetime
{
    private readonly StubObsServer _server = new("secret");
    private readonly FakeObs _obs = new();
    private readonly string _recordDirectory = Path.Combine(Path.GetTempPath(), $"cs1-studio-{Guid.NewGuid():N}");
    private ObsClient _client = null!;
    private StudioTools _tools = null!;

    public Task InitializeAsync()
    {
        _server.Handler = _obs.Handle;
        _client = new ObsClient(_server.Url, "secret", requestTimeout: TimeSpan.FromSeconds(3));
        _tools = new StudioTools(_client, new StudioSettings { RecordDirectory = _recordDirectory });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        if (Directory.Exists(_recordDirectory)) Directory.Delete(_recordDirectory, recursive: true);
    }

    [Fact]
    public async Task Status_ReportsSceneCaptureAndRecording()
    {
        var status = await _tools.StudioStatus();

        Assert.True((bool?)status!["ok"]);
        Assert.Equal("33.0.1", (string?)status["obs"]!["version"]);
        Assert.Equal("Scene", (string?)status["scene"]);
        Assert.False((bool?)status["onStudioScene"]);
        Assert.True((bool?)status["gameCapture"]!["captureActive"]);
        Assert.False((bool?)status["recording"]!["active"]);
        Assert.Equal(@"D:\rec", (string?)status["recordDirectory"]);
    }

    [Fact]
    public async Task Start_SwitchesToTheStudioSceneAndWaitsForTheRecording()
    {
        var result = await _tools.StudioRecord("start");

        Assert.True((bool?)result!["ok"]);
        Assert.Equal("CS1 Mayor", _obs.Scene);
        Assert.True(_obs.Recording);
        Assert.True((bool?)result["recording"]!["active"]);
        Assert.Null(result["warning"]);
    }

    [Fact]
    public async Task Start_WarnsWhenTheCaptureShowsNothing()
    {
        _obs.CaptureActive = false;

        var result = await _tools.StudioRecord("start");

        Assert.True((bool?)result!["ok"]);
        Assert.Contains("no picture", (string?)result["warning"]);
    }

    [Fact]
    public async Task Start_WhileRecording_IsRefusedWithoutTouchingObs()
    {
        _obs.Recording = true;

        var result = await _tools.StudioRecord("start");

        Assert.False((bool?)result!["ok"]);
        Assert.Contains("already recording", (string?)result["error"]);
        Assert.DoesNotContain("StartRecord", _server.Requests);
    }

    [Fact]
    public async Task Stop_ReturnsTheFilePath()
    {
        _obs.Recording = true;

        var result = await _tools.StudioRecord("stop");

        Assert.True((bool?)result!["ok"]);
        Assert.Equal(@"D:\rec\cs1-mayor 1.mp4", (string?)result["outputPath"]);
        Assert.False(_obs.Recording);
        Assert.False((bool?)result["recording"]!["active"]);
    }

    [Fact]
    public async Task Stop_WhenNotRecording_ReturnsObsRefusal()
    {
        var result = await _tools.StudioRecord("stop");

        Assert.False((bool?)result!["ok"]);
        Assert.Equal(501, (int?)result["code"]);
        Assert.Null(result["hint"]);   // OBS answered; no "is OBS running?" hint
    }

    [Fact]
    public async Task PauseAndResume()
    {
        _obs.Recording = true;

        var paused = await _tools.StudioRecord("Pause");
        Assert.True((bool?)paused!["recording"]!["paused"]);

        var resumed = await _tools.StudioRecord("resume");
        Assert.False((bool?)resumed!["recording"]!["paused"]);
    }

    [Fact]
    public async Task UnknownAction_IsAnError()
    {
        var result = await _tools.StudioRecord("rewind");

        Assert.False((bool?)result!["ok"]);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Marker_CreatesAChapterWithTheTimecode()
    {
        _obs.Recording = true;

        var result = await _tools.StudioMarker("  Water tower online ");

        Assert.True((bool?)result!["ok"]);
        Assert.Equal(["Water tower online"], _obs.Chapters);
        Assert.Equal("00:01:02.500", (string?)result["timecode"]);
    }

    [Fact]
    public async Task Marker_WaitsOutAShortPause()
    {
        _obs.Recording = true;
        _obs.Paused = true;
        _ = Task.Run(async () => { await Task.Delay(300); _obs.Paused = false; });

        var result = await _tools.StudioMarker("During a timelapse capture");

        Assert.True((bool?)result!["ok"]);
        Assert.Equal(["During a timelapse capture"], _obs.Chapters);
    }

    [Fact]
    public async Task Marker_OnAPausedRecording_SaysResume()
    {
        _obs.Recording = true;
        _obs.Paused = true;

        var result = await _tools.StudioMarker("Paused");

        Assert.False((bool?)result!["ok"]);
        Assert.Contains("resume", (string?)result["error"]);
        Assert.Empty(_obs.Chapters);
    }

    [Fact]
    public async Task Marker_WithoutRecording_ExplainsHowToStart()
    {
        var result = await _tools.StudioMarker("Day 1");

        Assert.False((bool?)result!["ok"]);
        Assert.Contains("studio_record", (string?)result["error"]);
        Assert.Empty(_obs.Chapters);
    }

    [Fact]
    public async Task Screenshot_ReturnsAJpegFromTheGameCapture()
    {
        var result = await _tools.StudioScreenshot(width: 5000);

        Assert.NotEqual(true, result.IsError);
        var image = Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.Equal("image/jpeg", image.MimeType);
        var request = Assert.Single(_obs.Screenshots);
        Assert.Equal("Game Capture: Cities", (string?)request!["sourceName"]);
        Assert.Equal(1920, (int?)request["imageWidth"]);   // clamped
    }

    [Fact]
    public async Task Screenshot_SaveAs_WritesAFullResolutionPng()
    {
        var result = await _tools.StudioScreenshot(saveAs: "day-1_before");

        Assert.NotEqual(true, result.IsError);
        var save = _obs.Screenshots[1]!;
        Assert.Equal("png", (string?)save["imageFormat"]);
        Assert.Equal(Path.Combine(_recordDirectory, "screenshots", "day-1_before.png"), (string?)save["imageFilePath"]);
    }

    [Fact]
    public async Task Screenshot_RejectsPathsInSaveAs()
    {
        var result = await _tools.StudioScreenshot(saveAs: @"..\evil");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Screenshot_WithoutPicture_IsAnError()
    {
        _obs.CaptureActive = false;

        var result = await _tools.StudioScreenshot();

        Assert.True(result.IsError);
        Assert.Empty(_obs.Screenshots);
    }

    [Fact]
    public async Task ObsDown_GivesAHint()
    {
        await _server.DisposeAsync();

        var status = await _tools.StudioStatus();

        Assert.False((bool?)status!["ok"]);
        Assert.Contains("setup-obs.ps1", (string?)status["hint"]);
    }
}
