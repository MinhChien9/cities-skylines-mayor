using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Camera work for the videos: smooth orbit / fly-over / zoom shots with the UI hidden,
/// and named viewpoints to return to (the fixed timelapse angles).
///
/// A shot is a stream of glide targets: the server samples a <see cref="CameraPath"/> at
/// ~30 Hz and posts each sample to <c>/commands/set-camera</c> with <c>instant:false</c>;
/// the game's camera lerps towards the moving target, which smooths the steps. The
/// camera is a render-only object, so this never touches city state (the bridge runs it
/// on the Unity main thread, the named exception to the simulation-thread rule).
/// </summary>
[McpServerToolType]
public sealed class CinematicTools
{
    private const int StepMilliseconds = 33;

    private readonly BridgeClient _bridge;
    private readonly ViewpointStore _viewpoints;

    public CinematicTools(BridgeClient bridge, ViewpointStore viewpoints)
    {
        _bridge = bridge;
        _viewpoints = viewpoints;
    }

    // -------------------------------------------------------------------- shots

    [Description("A cinematic camera move for B-roll, played in real time (the call returns when the shot is done). kind: 'orbit' circles the point (x, z) by `degrees` of yaw; 'flyover' travels from (x, z) to (toX, toZ) facing the direction of travel; 'zoom' pushes in or pulls out on (x, z) to `toDistance`. Unset framing values keep the current camera. The game UI, notification icons and the bridge console are hidden for the shot (hideUi, default true) and restored afterwards - unless they were already hidden, so for a series of shots hide the UI once with city_camera hideUi:true and show it again at the end. Start the recording first (studio_record) if the shot is for the video; a studio_marker before it makes it easy to find. Typical: orbit 90 degrees over 12 s at distance 500, pitch 30.")]
    [McpServerTool(Name = "studio_camera_move", Title = "Camera Shot")]
    public async Task<JsonNode?> StudioCameraMove(
        [Description("'orbit', 'flyover' or 'zoom'.")] string kind,
        [Description("World x of the point to frame (orbit/zoom centre, flyover start). Default: the current camera target.")] double? x = null,
        [Description("World z of the point to frame.")] double? z = null,
        [Description("Flyover end x.")] double? toX = null,
        [Description("Flyover end z.")] double? toZ = null,
        [Description("Camera distance at the start (100-4000). Default: current.")] double? distance = null,
        [Description("Camera distance at the end (zoom target; optional for flyover).")] double? toDistance = null,
        [Description("Pitch in degrees, 0 = horizon, 90 = top-down. Default: current. 20-40 looks cinematic.")] double? verticalAngle = null,
        [Description("Starting yaw in degrees (orbit/zoom). Default: current.")] double? horizontalAngle = null,
        [Description("Orbit sweep in degrees, negative = the other way (default 90).")] double degrees = 90,
        [Description("Duration in seconds (2-60, default 10).")] double seconds = 10,
        [Description("Hide the UI during the shot and restore it after (default true).")] bool hideUi = true,
        CancellationToken cancellationToken = default)
    {
        string move = kind.Trim().ToLowerInvariant();
        if (move is not ("orbit" or "flyover" or "zoom"))
        {
            return Fail($"Unknown kind '{kind}'. Use orbit, flyover or zoom.");
        }
        if (move == "flyover" && (toX is null || toZ is null))
        {
            return Fail("A flyover needs toX and toZ.");
        }
        if (move == "zoom" && toDistance is null)
        {
            return Fail("A zoom needs toDistance.");
        }

        try
        {
            var state = await _bridge.PostAsync("/commands/set-camera", new Dictionary<string, object?>(), cancellationToken)
                .ConfigureAwait(false);
            if (state?["ok"]?.GetValue<bool>() != true)
            {
                return state;
            }
            var current = ReadFrame(state);
            bool uiWasHidden = (bool?)state["camera"]?["uiHidden"] ?? false;

            var start = new CameraFrame(
                x ?? current.X, current.Y, z ?? current.Z,
                Math.Clamp(distance ?? current.Distance, 100, 4000),
                Math.Clamp(verticalAngle ?? current.VerticalAngle, 0, 90),
                CameraPath.WrapYaw(horizontalAngle ?? current.HorizontalAngle));

            CameraPath path = move switch
            {
                "orbit" => CameraPath.Orbit(start, degrees),
                "flyover" => CameraPath.Flyover(start, toX!.Value, toZ!.Value, toDistance is null ? null : Math.Clamp(toDistance.Value, 100, 4000)),
                _ => CameraPath.Zoom(start, Math.Clamp(toDistance!.Value, 100, 4000)),
            };
            double duration = Math.Clamp(seconds, 2, 60);

            var result = await PlayAsync(path, duration, hideUi, uiWasHidden, cancellationToken).ConfigureAwait(false);
            result["kind"] = move;
            return result;
        }
        catch (BridgeException ex)
        {
            return BridgeError(ex);
        }
    }

    private async Task<JsonObject> PlayAsync(CameraPath path, double seconds, bool hideUi, bool uiWasHidden,
        CancellationToken cancellationToken)
    {
        // Cut to the start framing (and hide the UI in the same call).
        var first = path.Start.ToBody(instant: true);
        if (hideUi)
        {
            first["hideUi"] = true;
        }
        await _bridge.PostAsync("/commands/set-camera", first, cancellationToken).ConfigureAwait(false);

        int steps = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                double t = clock.Elapsed.TotalSeconds / seconds;
                await _bridge.PostAsync("/commands/set-camera", path.At(t).ToBody(instant: false), cancellationToken)
                    .ConfigureAwait(false);
                steps++;
                if (t >= 1)
                {
                    break;
                }
                await Task.Delay(StepMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            // Let the game's smoothing settle on the final framing.
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (hideUi && !uiWasHidden)
            {
                await _bridge.PostAsync("/commands/set-camera", new Dictionary<string, object?> { ["hideUi"] = false },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        var end = path.At(1);
        return new JsonObject
        {
            ["ok"] = true,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2),
            ["steps"] = steps,
            ["from"] = FrameJson(path.Start),
            ["to"] = FrameJson(end),
            ["uiRestored"] = hideUi && !uiWasHidden,
        };
    }

    // --------------------------------------------------------------- viewpoints

    [Description("Named camera viewpoints - fixed angles to come back to (timelapse frames, before/after shots, the establishing shot of an episode). action: 'save' stores the current camera under `name` (frame it first with city_camera); 'go' moves the camera there (glide:true for a smooth move, hideUi to hide the UI); 'list' shows them all; 'delete' removes one. Stored in viewpoints.local.json at the repo root.")]
    [McpServerTool(Name = "studio_viewpoint", Title = "Viewpoints")]
    public async Task<JsonNode?> StudioViewpoint(
        [Description("'save', 'go', 'list' or 'delete'.")] string action,
        [Description("Viewpoint name (letters, digits, '-', '_'), e.g. 'downtown-overview'.")] string? name = null,
        [Description("go: glide instead of cutting (default false).")] bool glide = false,
        [Description("go: true/false to hide/show the UI; omit to leave it.")] bool? hideUi = null,
        CancellationToken cancellationToken = default)
    {
        string verb = action.Trim().ToLowerInvariant();
        if (verb == "list")
        {
            return new JsonObject { ["ok"] = true, ["file"] = _viewpoints.FilePath, ["viewpoints"] = _viewpoints.All() };
        }
        if (verb is not ("save" or "go" or "delete"))
        {
            return Fail($"Unknown action '{action}'. Use save, go, list or delete.");
        }
        if (!ViewpointStore.IsValidName(name))
        {
            return Fail("A viewpoint needs a name of letters, digits, '-' or '_' (max 60).");
        }

        try
        {
            switch (verb)
            {
                case "save":
                {
                    var state = await _bridge.PostAsync("/commands/set-camera", new Dictionary<string, object?>(), cancellationToken)
                        .ConfigureAwait(false);
                    if (state?["ok"]?.GetValue<bool>() != true)
                    {
                        return state;
                    }
                    var frame = ReadFrame(state);
                    _viewpoints.Save(name!, frame);
                    return new JsonObject { ["ok"] = true, ["saved"] = name, ["viewpoint"] = FrameJson(frame) };
                }
                case "delete":
                    return _viewpoints.Delete(name!)
                        ? new JsonObject { ["ok"] = true, ["deleted"] = name }
                        : Fail($"No viewpoint named '{name}'.");
                default:
                {
                    if (_viewpoints.Get(name!) is not { } frame)
                    {
                        return Fail($"No viewpoint named '{name}'. studio_viewpoint action='list' shows them.");
                    }
                    var body = frame.ToBody(instant: !glide);
                    if (hideUi.HasValue)
                    {
                        body["hideUi"] = hideUi.Value;
                    }
                    var moved = await _bridge.PostAsync("/commands/set-camera", body, cancellationToken).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = moved?["ok"]?.GetValue<bool>() == true,
                        ["went"] = name,
                        ["viewpoint"] = FrameJson(frame),
                        ["camera"] = moved?["camera"]?.DeepClone(),
                    };
                }
            }
        }
        catch (BridgeException ex)
        {
            return BridgeError(ex);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static CameraFrame ReadFrame(JsonNode state)
    {
        var camera = state["camera"];
        var target = camera?["target"];
        return new CameraFrame(
            (double?)target?["x"] ?? 0,
            (double?)target?["y"] ?? 0,
            (double?)target?["z"] ?? 0,
            (double?)camera?["distance"] ?? 600,
            (double?)camera?["angle"]?["vertical"] ?? 45,
            (double?)camera?["angle"]?["horizontal"] ?? 0);
    }

    internal static JsonObject FrameJson(CameraFrame f) => new()
    {
        ["x"] = Math.Round(f.X, 1),
        ["y"] = Math.Round(f.Y, 1),
        ["z"] = Math.Round(f.Z, 1),
        ["distance"] = Math.Round(f.Distance, 1),
        ["verticalAngle"] = Math.Round(f.VerticalAngle, 1),
        ["horizontalAngle"] = Math.Round(f.HorizontalAngle, 1),
    };

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    private static JsonObject BridgeError(BridgeException ex) => new()
    {
        ["ok"] = false,
        ["error"] = ex.Message,
        ["hint"] = "Start Cities: Skylines with the CS1 Mayor Bridge mod enabled and load a city, then retry. See city_health.",
    };
}

/// <summary>
/// Named camera framings in <c>viewpoints.local.json</c> (git-ignored; they belong to one
/// city). Read and written whole on every change: the file is tiny.
/// </summary>
public sealed class ViewpointStore
{
    private readonly object _gate = new();

    public ViewpointStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary><c>viewpoints.local.json</c> next to the journal folder (the repo root).</summary>
    public static ViewpointStore Default()
        => new(Path.Combine(Path.GetDirectoryName(Journal.DefaultDirectory())!, "viewpoints.local.json"));

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 60
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public JsonObject All()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public CameraFrame? Get(string name)
    {
        lock (_gate)
        {
            var v = Load()[name];
            if (v is null)
            {
                return null;
            }
            return new CameraFrame((double)v["x"]!, (double?)v["y"] ?? 0, (double)v["z"]!, (double)v["distance"]!,
                (double)v["verticalAngle"]!, (double)v["horizontalAngle"]!);
        }
    }

    public void Save(string name, CameraFrame frame)
    {
        lock (_gate)
        {
            var all = Load();
            var entry = CinematicTools.FrameJson(frame);
            entry["savedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            all[name] = entry;
            Write(all);
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(name))
            {
                return false;
            }
            Write(all);
            return true;
        }
    }

    private JsonObject Load()
    {
        if (!File.Exists(FilePath))
        {
            return new JsonObject();
        }
        return JsonNode.Parse(File.ReadAllText(FilePath))?["viewpoints"] as JsonObject is { } v
            ? (JsonObject)v.DeepClone()
            : new JsonObject();
    }

    private void Write(JsonObject all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var root = new JsonObject { ["viewpoints"] = all };
        File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
