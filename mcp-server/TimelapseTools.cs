using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>The agent's handle on <see cref="TimelapseRunner"/>.</summary>
[McpServerToolType]
public sealed class TimelapseTools
{
    private readonly TimelapseRunner _runner;

    public TimelapseTools(TimelapseRunner runner)
    {
        _runner = runner;
    }

    [Description("Timelapse of the city growing: every `everyDays` game days, a full-resolution frame from each named viewpoint (save them first with studio_viewpoint; 1-3 wide, high angles work best, e.g. 'overview'). Runs in the background while the city simulates (speed 3, unpaused); game time only advances when unpaused. Each capture briefly takes the camera (UI hidden, cut to each viewpoint, PNG through OBS, then camera and UI restored) and pauses an active recording around it, so the video never shows the jumps. action: 'start', 'stop', 'status', or 'capture' (one round now - e.g. a 'before' frame; with viewpoints when nothing is running). Frames: <recording folder>/timelapse/<session>/<viewpoint>/ with frames.jsonl.")]
    [McpServerTool(Name = "studio_timelapse", Title = "Timelapse")]
    public async Task<JsonNode?> StudioTimelapse(
        [Description("'start', 'stop', 'status' or 'capture'.")] string action,
        [Description("Viewpoint names (start; or capture when no timelapse runs).")] string[]? viewpoints = null,
        [Description("Game days between captures (0.25-365, default 7).")] double everyDays = 7,
        CancellationToken cancellationToken = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                return _runner.Start(viewpoints ?? [], everyDays);
            case "stop":
                return await _runner.StopAsync().ConfigureAwait(false);
            case "status":
                return _runner.Status();
            case "capture":
                try
                {
                    return await _runner.CaptureNowAsync(viewpoints, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is BridgeException or ObsException or InvalidOperationException)
                {
                    return new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = ex.Message,
                        ["hint"] = "A capture needs the game (city loaded) and OBS; check city_health and studio_status.",
                    };
                }
            default:
                return new JsonObject { ["ok"] = false, ["error"] = $"Unknown action '{action}'. Use start, stop, status or capture." };
        }
    }
}
