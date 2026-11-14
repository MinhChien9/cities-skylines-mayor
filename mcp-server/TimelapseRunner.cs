using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Timelapse capture: every N game days, a full-resolution frame from each of a few fixed
/// viewpoints (<see cref="ViewpointStore"/>), saved through OBS's game capture.
///
/// It runs in the background of the MCP server while the city simulates. A capture
/// briefly takes the camera: it hides the UI, cuts to each viewpoint, waits for the frame
/// to settle, saves a PNG, then puts the camera and UI back exactly as they were. If OBS
/// is recording, the recording is paused around the capture so the video never shows the
/// jumps. Frames land in <c>&lt;recordings&gt;/timelapse/&lt;session&gt;/&lt;viewpoint&gt;/</c>
/// with a <c>frames.jsonl</c> manifest (game date, wall time, file) for assembly.
///
/// Game time only moves when the city is unpaused, so a paused city captures nothing.
/// </summary>
public sealed class TimelapseRunner : IAsyncDisposable
{
    private readonly BridgeClient _bridge;
    private readonly ObsClient _obs;
    private readonly StudioSettings _settings;
    private readonly ViewpointStore _viewpoints;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _guard;
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Session? _session;

    public TimelapseRunner(BridgeClient bridge, ObsClient obs, StudioSettings settings, ViewpointStore viewpoints,
        TimeSpan? pollInterval = null, TimeSpan? settle = null, TimeSpan? recordingGuard = null)
    {
        _bridge = bridge;
        _obs = obs;
        _settings = settings;
        _viewpoints = viewpoints;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _settle = settle ?? TimeSpan.FromMilliseconds(1500);
        // The game capture runs a few frames behind the game, and camera/UI changes land a
        // frame after the bridge call returns: without a margin on both sides of the pause,
        // ~0.35 s of the cut leaked into the recording (measured live).
        _guard = recordingGuard ?? TimeSpan.FromMilliseconds(700);
    }

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string Folder { get; init; }
        public required string[] Viewpoints { get; init; }
        public required double EveryDays { get; init; }
        public DateTime? LastCaptureGameTime { get; set; }
        public int Captures { get; set; }
        public int Frames { get; set; }
        public string? LastError { get; set; }
        public bool Running { get; set; }
    }

    public bool IsRunning
    {
        get { lock (_gate) { return _session?.Running == true; } }
    }

    /// <summary>Starts a timelapse; the first capture happens on the first poll.</summary>
    public JsonObject Start(IReadOnlyList<string> viewpoints, double everyDays)
    {
        lock (_gate)
        {
            if (_session?.Running == true)
            {
                return Fail("A timelapse is already running; stop it first.", Status());
            }
            var missing = viewpoints.Where(v => _viewpoints.Get(v) is null).ToList();
            if (viewpoints.Count == 0 || missing.Count > 0)
            {
                return Fail(viewpoints.Count == 0
                    ? "Name at least one viewpoint (studio_viewpoint action='save' first)."
                    : $"Unknown viewpoint(s): {string.Join(", ", missing)}. studio_viewpoint action='list' shows them.");
            }

            string id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _session = new Session
            {
                Id = id,
                Folder = Path.Combine(RecordRoot(), "timelapse", id),
                Viewpoints = viewpoints.ToArray(),
                EveryDays = Math.Clamp(everyDays, 0.25, 365),
                Running = true,
            };
            Directory.CreateDirectory(_session.Folder);
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
            var status = Status();
            status["started"] = true;
            return status;
        }
    }

    public async Task<JsonObject> StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_session?.Running != true)
            {
                return Fail("No timelapse is running.", Status());
            }
            _session.Running = false;
            _cts?.Cancel();
            loop = _loop;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        var status = Status();
        status["stopped"] = true;
        return status;
    }

    public JsonObject Status()
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return new JsonObject { ["ok"] = true, ["running"] = false };
            }
            return new JsonObject
            {
                ["ok"] = true,
                ["running"] = _session.Running,
                ["session"] = _session.Id,
                ["folder"] = _session.Folder,
                ["viewpoints"] = new JsonArray(_session.Viewpoints.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["everyDays"] = _session.EveryDays,
                ["captures"] = _session.Captures,
                ["frames"] = _session.Frames,
                ["lastCaptureGameTime"] = _session.LastCaptureGameTime?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["lastError"] = _session.LastError,
            };
        }
    }

    /// <summary>
    /// Captures one round now (every viewpoint of the running session, or the given ones
    /// into a one-off folder when nothing is running).
    /// </summary>
    public async Task<JsonObject> CaptureNowAsync(IReadOnlyList<string>? viewpoints, CancellationToken cancellationToken)
    {
        Session session;
        lock (_gate)
        {
            if (_session?.Running == true && (viewpoints is null || viewpoints.Count == 0))
            {
                session = _session;
            }
            else
            {
                var names = viewpoints?.ToArray() ?? [];
                var missing = names.Where(v => _viewpoints.Get(v) is null).ToList();
                if (names.Length == 0 || missing.Count > 0)
                {
                    return Fail(names.Length == 0 ? "Name at least one viewpoint." : $"Unknown viewpoint(s): {string.Join(", ", missing)}.");
                }
                string id = "manual-" + DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                session = new Session
                {
                    Id = id,
                    Folder = Path.Combine(RecordRoot(), "timelapse", id),
                    Viewpoints = names,
                    EveryDays = 0,
                };
            }
        }
        var gameTime = await ReadGameTimeAsync(cancellationToken).ConfigureAwait(false);
        var files = await CaptureAsync(session, gameTime, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["gameTime"] = gameTime?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        };
    }

    /// <summary>Whether enough game time has passed since the last capture.</summary>
    public static bool IsDue(DateTime? lastCapture, DateTime now, double everyDays)
        => lastCapture is null || (now - lastCapture.Value).TotalDays >= everyDays;

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Session? session;
            lock (_gate) { session = _session; }
            if (session is null || !session.Running)
            {
                return;
            }
            try
            {
                var now = await ReadGameTimeAsync(cancellationToken).ConfigureAwait(false);
                if (now is { } gameTime && IsDue(session.LastCaptureGameTime, gameTime, session.EveryDays))
                {
                    await CaptureAsync(session, gameTime, cancellationToken).ConfigureAwait(false);
                    lock (_gate) { session.LastCaptureGameTime = gameTime; }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate) { session.LastError = $"{DateTimeOffset.Now:HH:mm:ss} {ex.Message}"; }
            }
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<List<string>> CaptureAsync(Session session, DateTime? gameTime, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await _bridge.PostAsync("/commands/set-camera", new Dictionary<string, object?>(), cancellationToken)
                .ConfigureAwait(false);
            if (before?["ok"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("Could not read the camera: " + before?.ToJsonString());
            }
            var original = CinematicTools.ReadFrame(before);
            bool uiWasHidden = (bool?)before["camera"]?["uiHidden"] ?? false;

            var record = await _obs.RequestAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
            bool pauseRecording = ((bool?)record?["outputActive"] ?? false) && !((bool?)record?["outputPaused"] ?? false);
            if (pauseRecording)
            {
                await _obs.RequestAsync("PauseRecord", null, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_guard, cancellationToken).ConfigureAwait(false);
            }

            int index;
            lock (_gate) { index = session.Captures + 1; }
            string stamp = gameTime?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown-date";
            try
            {
                foreach (string name in session.Viewpoints)
                {
                    if (_viewpoints.Get(name) is not { } frame)
                    {
                        continue;
                    }
                    var body = frame.ToBody(instant: true);
                    body["hideUi"] = true;
                    await _bridge.PostAsync("/commands/set-camera", body, cancellationToken).ConfigureAwait(false);
                    await Task.Delay(_settle, cancellationToken).ConfigureAwait(false);

                    string folder = Path.Combine(session.Folder, name);
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, $"{index:D4}_{stamp}.png");
                    await _obs.RequestAsync("SaveSourceScreenshot", new JsonObject
                    {
                        ["sourceName"] = _settings.GameCapture,
                        ["imageFormat"] = "png",
                        ["imageFilePath"] = file,
                    }, cancellationToken).ConfigureAwait(false);
                    files.Add(file);
                    await AppendManifestAsync(session, new JsonObject
                    {
                        ["capture"] = index,
                        ["viewpoint"] = name,
                        ["gameTime"] = gameTime?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                        ["wall"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                        ["file"] = file,
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                // Put everything back even if a capture failed half-way.
                var restore = original.ToBody(instant: true);
                restore["hideUi"] = uiWasHidden;
                await _bridge.PostAsync("/commands/set-camera", restore, CancellationToken.None).ConfigureAwait(false);
                if (pauseRecording)
                {
                    await Task.Delay(_guard, CancellationToken.None).ConfigureAwait(false);
                    await _obs.RequestAsync("ResumeRecord", null, CancellationToken.None).ConfigureAwait(false);
                }
            }

            lock (_gate)
            {
                session.Captures = index;
                session.Frames += files.Count;
                session.LastError = null;
            }
            return files;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private async Task<DateTime?> ReadGameTimeAsync(CancellationToken cancellationToken)
    {
        var summary = await _bridge.GetAsync("/state/summary", cancellationToken).ConfigureAwait(false);
        string? text = (string?)summary?["gameTime"];
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    private static async Task AppendManifestAsync(Session session, JsonObject line)
    {
        Directory.CreateDirectory(session.Folder);
        await File.AppendAllTextAsync(Path.Combine(session.Folder, "frames.jsonl"),
            line.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n", new UTF8Encoding(false))
            .ConfigureAwait(false);
    }

    private string RecordRoot()
        => _settings.RecordDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "cs1-mayor");

    private static JsonObject Fail(string error, JsonObject? status = null)
    {
        var result = new JsonObject { ["ok"] = false, ["error"] = error };
        if (status is not null)
        {
            result["status"] = status;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}
