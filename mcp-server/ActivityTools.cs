using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Reads the <see cref="ActivityLog"/> back: what the mayor changed this session, with the
/// game date, population and cash at each step. Entries are written automatically.
/// </summary>
[McpServerToolType]
public sealed class ActivityTools
{
    private readonly ActivityLog _log;

    public ActivityTools(ActivityLog log)
    {
        _log = log;
    }

    [Description("Read back the newest entries of this session's activity log: every build and office action (roads, buildings, zoning, bulldozing, taxes, budgets, policies, saves, ...) with its arguments, a compact result and the game date, population and cash at that moment. Dry runs and reads are not logged; refusals are. Use it to recall what you already did, e.g. after a context reset. The log is a plain JSONL file (activity/<session>.jsonl) that other tools can follow.")]
    [McpServerTool(Name = "city_activity", Title = "Activity Log (recent)", ReadOnly = true)]
    public async Task<JsonNode?> CityActivity(
        [Description("How many of the newest entries (1-200, default 20).")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var entries = await _log.ReadRecentAsync(Math.Clamp(limit, 1, 200), cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["session"] = _log.SessionId,
            ["file"] = _log.FilePath,
            ["entries"] = entries,
        };
    }
}
