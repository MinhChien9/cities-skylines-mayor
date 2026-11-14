using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The studio: recording the mayor at work through OBS (obs-websocket v5).
///
/// OBS is prepared by <c>scripts/setup-obs.ps1</c> (scene "CS1 Mayor" with a Game Capture
/// of Cities.exe, NVENC hybrid MP4). These tools only drive it: status, record
/// start/stop/pause/resume, chapter markers inside the recording, and a real rendered
/// frame of the game when the semantic <c>city_map</c> is not enough.
///
/// Recording is outward-facing state: a dropped connection after <c>StartRecord</c> may
/// still have started a recording, so callers re-read <c>studio_status</c> rather than
/// retrying (see <see cref="ObsClient"/>).
/// </summary>
[McpServerToolType]
public sealed class StudioTools
{
    private const string Hint =
        "Is OBS running? Start it (or run scripts/setup-obs.ps1, which also configures it), then check studio_status.";

    private readonly ObsClient _obs;
    private readonly StudioSettings _settings;

    public StudioTools(ObsClient obs, StudioSettings settings)
    {
        _obs = obs;
        _settings = settings;
    }

    // ------------------------------------------------------------------- status

    [Description("Studio status in one call: is OBS reachable, which scene is live, is the game capture showing the game (captureActive), and the recording state (active, paused, timecode, bytes) plus the recording folder. Check it before recording and whenever a studio call failed.")]
    [McpServerTool(Name = "studio_status", Title = "Studio Status", ReadOnly = true)]
    public async Task<JsonNode?> StudioStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await _obs.RequestAsync("GetVersion", null, cancellationToken).ConfigureAwait(false);
            var scene = await _obs.RequestAsync("GetCurrentProgramScene", null, cancellationToken).ConfigureAwait(false);
            var directory = await _obs.RequestAsync("GetRecordDirectory", null, cancellationToken).ConfigureAwait(false);
            string? currentScene = (string?)scene?["sceneName"] ?? (string?)scene?["currentProgramSceneName"];

            return new JsonObject
            {
                ["ok"] = true,
                ["obs"] = new JsonObject
                {
                    ["version"] = (string?)version?["obsVersion"],
                    ["websocket"] = (string?)version?["obsWebSocketVersion"],
                    ["url"] = _obs.Url.ToString(),
                },
                ["scene"] = currentScene,
                ["onStudioScene"] = currentScene == _settings.Scene,
                ["gameCapture"] = new JsonObject
                {
                    ["name"] = _settings.GameCapture,
                    ["captureActive"] = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false),
                },
                ["recording"] = await RecordingAsync(cancellationToken).ConfigureAwait(false),
                ["recordDirectory"] = (string?)directory?["recordDirectory"],
                ["settingsFile"] = _settings.SourcePath,
            };
        }
        catch (ObsException ex)
        {
            return Error(ex);
        }
    }

    // ---------------------------------------------------------------- recording

    [Description("Control the OBS recording. action: 'start' (switches OBS to the studio scene first; warns when the game capture shows no picture), 'stop' (returns the file path), 'pause', 'resume'. Recordings are NVENC H.264 hybrid MP4 in the recording folder; markers from studio_marker become chapters in the file. If a call fails, read studio_status before retrying - OBS may already have acted.")]
    [McpServerTool(Name = "studio_record", Title = "Record")]
    public async Task<JsonNode?> StudioRecord(
        [Description("'start', 'stop', 'pause' or 'resume'.")] string action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "start":
                    return await StartAsync(cancellationToken).ConfigureAwait(false);
                case "stop":
                {
                    var stopped = await _obs.RequestAsync("StopRecord", null, cancellationToken).ConfigureAwait(false);
                    // OBS finalises the file after answering; report once it has.
                    var recording = await WaitForRecordingAsync(active: false, cancellationToken).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["action"] = "stop",
                        ["outputPath"] = (string?)stopped?["outputPath"],
                        ["recording"] = recording,
                    };
                }
                case "pause":
                case "resume":
                {
                    string request = action.Trim().Equals("pause", StringComparison.OrdinalIgnoreCase) ? "PauseRecord" : "ResumeRecord";
                    await _obs.RequestAsync(request, null, cancellationToken).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["action"] = action.Trim().ToLowerInvariant(),
                        ["recording"] = await RecordingAsync(cancellationToken).ConfigureAwait(false),
                    };
                }
                default:
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = $"Unknown action '{action}'. Use start, stop, pause or resume.",
                    };
            }
        }
        catch (ObsException ex)
        {
            return Error(ex);
        }
    }

    private async Task<JsonNode?> StartAsync(CancellationToken cancellationToken)
    {
        var before = await RecordingAsync(cancellationToken).ConfigureAwait(false);
        if ((bool?)before["active"] == true)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "OBS is already recording.",
                ["recording"] = before,
            };
        }

        await _obs.RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = _settings.Scene }, cancellationToken)
            .ConfigureAwait(false);
        bool? captureActive = await CaptureActiveAsync(cancellationToken).ConfigureAwait(false);
        await _obs.RequestAsync("StartRecord", null, cancellationToken).ConfigureAwait(false);

        // StartRecord returns before the output is running; wait briefly for it.
        var recording = await WaitForRecordingAsync(active: true, cancellationToken).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["ok"] = (bool?)recording["active"] == true,
            ["action"] = "start",
            ["scene"] = _settings.Scene,
            ["recording"] = recording,
        };
        if (captureActive != true)
        {
            result["warning"] = "The game capture shows no picture (is Cities.exe running and not minimised?). The recording may be black.";
        }
        if ((bool?)recording["active"] != true)
        {
            result["error"] = "OBS accepted StartRecord but the recording is not active yet; check studio_status.";
        }
        return result;
    }

    // ------------------------------------------------------------------ markers

    [Description("Drop a chapter marker into the running recording at the current moment (CreateRecordChapter; hybrid MP4 stores it, editors and ffprobe show it). Use it at story beats: a decision, a mistake, a milestone, a before/after. The name becomes the chapter title, so make it readable, e.g. 'Water tower online' or 'Traffic jam at the bridge'. Requires an active recording.")]
    [McpServerTool(Name = "studio_marker", Title = "Chapter Marker")]
    public async Task<JsonNode?> StudioMarker(
        [Description("Chapter title (short, human-readable).")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new JsonObject { ["ok"] = false, ["error"] = "A marker needs a name." };
        }
        try
        {
            var recording = await RecordingAsync(cancellationToken).ConfigureAwait(false);
            if ((bool?)recording["active"] != true)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "Not recording. Markers are chapters inside a recording; start one with studio_record action='start'.",
                };
            }
            await _obs.RequestAsync("CreateRecordChapter", new JsonObject { ["chapterName"] = name.Trim() }, cancellationToken)
                .ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = true,
                ["name"] = name.Trim(),
                ["timecode"] = recording["timecode"]?.DeepClone(),
            };
        }
        catch (ObsException ex)
        {
            return Error(ex);
        }
    }

    // -------------------------------------------------------------- screenshot

    [Description("A real rendered frame of the game, taken from the OBS game capture (not the semantic city_map): what the viewer sees, including the game UI. Use it to judge how a shot looks before recording or to check something the map cannot show. Costs image tokens; prefer city_map for planning. saveAs also writes the full-resolution PNG to <recording folder>/screenshots/<saveAs>.png (thumbnails, B-roll stills).")]
    [McpServerTool(Name = "studio_screenshot", Title = "Game Frame", ReadOnly = true)]
    public async Task<CallToolResult> StudioScreenshot(
        [Description("Image width in pixels (320-1920, default 1280); the height keeps the game's aspect ratio.")] int width = 1280,
        [Description("Optional file name (letters, digits, '-', '_') to also save a full-resolution PNG.")] string? saveAs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await CaptureActiveAsync(cancellationToken).ConfigureAwait(false) != true)
            {
                return ErrorResult($"The game capture '{_settings.GameCapture}' shows no picture. Is Cities.exe running and not minimised? Check studio_status.");
            }

            var shot = await _obs.RequestAsync("GetSourceScreenshot", new JsonObject
            {
                ["sourceName"] = _settings.GameCapture,
                ["imageFormat"] = "jpg",
                ["imageWidth"] = Math.Clamp(width, 320, 1920),
                ["imageCompressionQuality"] = 85,
            }, cancellationToken).ConfigureAwait(false);
            string data = (string?)shot?["imageData"] ?? "";
            int comma = data.IndexOf(',');
            byte[] jpeg = Convert.FromBase64String(comma >= 0 ? data[(comma + 1)..] : data);

            var info = new JsonObject { ["source"] = _settings.GameCapture, ["width"] = Math.Clamp(width, 320, 1920) };
            if (!string.IsNullOrWhiteSpace(saveAs))
            {
                string? path = ScreenshotPath(saveAs);
                if (path is null)
                {
                    return ErrorResult("saveAs may only contain letters, digits, '-' and '_'.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await _obs.RequestAsync("SaveSourceScreenshot", new JsonObject
                {
                    ["sourceName"] = _settings.GameCapture,
                    ["imageFormat"] = "png",
                    ["imageFilePath"] = path,
                }, cancellationToken).ConfigureAwait(false);
                info["savedTo"] = path;
            }

            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(jpeg, "image/jpeg"),
                    new TextContentBlock { Text = info.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) },
                ],
            };
        }
        catch (ObsException ex)
        {
            return ErrorResult($"{ex.Message} {Hint}");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The full-resolution screenshot path for a sanitised name, or null.</summary>
    internal string? ScreenshotPath(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 80 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }
        string root = _settings.RecordDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "cs1-mayor");
        return Path.Combine(root, "screenshots", trimmed + ".png");
    }

    private async Task<bool?> CaptureActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = await _obs.RequestAsync("GetSourceActive", new JsonObject { ["sourceName"] = _settings.GameCapture },
                cancellationToken).ConfigureAwait(false);
            return (bool?)active?["videoActive"];
        }
        catch (ObsException ex) when (ex.Code is not null)
        {
            return null;   // the input does not exist (run setup-obs.ps1)
        }
    }

    /// <summary>Polls the record status (up to ~5 s) until it reaches the wanted state.</summary>
    private async Task<JsonObject> WaitForRecordingAsync(bool active, CancellationToken cancellationToken)
    {
        JsonObject recording = await RecordingAsync(cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < 50 && (bool?)recording["active"] != active; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            recording = await RecordingAsync(cancellationToken).ConfigureAwait(false);
        }
        return recording;
    }

    private async Task<JsonObject> RecordingAsync(CancellationToken cancellationToken)
    {
        var status = await _obs.RequestAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["active"] = (bool?)status?["outputActive"] ?? false,
            ["paused"] = (bool?)status?["outputPaused"] ?? false,
            ["timecode"] = (string?)status?["outputTimecode"],
            ["durationMs"] = (long?)status?["outputDuration"],
            ["bytes"] = (long?)status?["outputBytes"],
        };
    }

    private static JsonObject Error(ObsException ex)
    {
        var error = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        if (ex.Code is { } code)
        {
            error["code"] = code;
        }
        if (ex.Code is null or ObsClient.CloseAuthenticationFailed)
        {
            error["hint"] = Hint;
        }
        return error;
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
