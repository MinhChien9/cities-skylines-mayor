using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The activity log: one JSON line per action that changed the city (builds, office
/// levers, saves). One file per MCP server session:
/// <c>activity/&lt;yyyyMMdd-HHmmss&gt;.jsonl</c> at the repo root (git-ignored).
///
/// Every entry carries the wall time and the game date, population and cash at that
/// moment, the tool, its arguments and a compact result. It is the mayor's audit trail
/// (<c>city_activity</c> reads it back after a context reset) and a plain file other tools
/// can follow - the video studio (a separate repo) tails it to stamp each action with the
/// recording timecode. The game read is best effort with a short timeout: a closed game
/// never blocks the mayor, the field is just null.
///
/// Entries are written by <see cref="ToolCallFilter"/> after every mutating tool call.
/// </summary>
public sealed class ActivityLog
{
    /// <summary>Tools logged automatically, and the entry kind each produces.</summary>
    public static readonly IReadOnlyDictionary<string, string> JournalledTools = new Dictionary<string, string>
    {
        ["city_build_road"] = "build",
        ["city_place_building"] = "build",
        ["city_move_building"] = "build",
        ["city_zone_segment"] = "build",
        ["city_zone_area"] = "build",
        ["city_bulldoze"] = "build",
        ["city_set_tax_rate"] = "office",
        ["city_set_budget"] = "office",
        ["city_set_policy"] = "office",
        ["city_set_building_active"] = "office",
        ["city_empty_building"] = "office",
        ["city_unlock_area"] = "office",
        ["city_set_simulation_speed"] = "office",
        ["city_transport_line"] = "office",
        ["city_district"] = "office",
        ["city_save"] = "office",
        ["city_request"] = "office",      // only its POSTs, see ShouldJournal
    };

    private const int MaxResultChars = 2000;
    private static readonly string[] ResultKeys =
        ["ok", "error", "errors", "hint", "cost", "segmentIds", "nodeIds", "buildingId", "position", "snappedTo", "name", "outputPath", "timecode", "count"];

    private readonly BridgeClient? _bridge;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _contextTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _sequence;

    public ActivityLog(string directory, BridgeClient? bridge,
        Func<DateTimeOffset>? clock = null, TimeSpan? contextTimeout = null)
    {
        Directory = directory;
        _bridge = bridge;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _contextTimeout = contextTimeout ?? TimeSpan.FromSeconds(2);
        SessionId = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    public string Directory { get; }

    public string SessionId { get; }

    public string FilePath => Path.Combine(Directory, SessionId + ".jsonl");

    /// <summary>
    /// The log folder: <c>$CS1_ACTIVITY_DIR</c>, else <c>activity/</c> in the repo root
    /// (the folder holding <c>Cs1Mayor.sln</c>, searched upwards from the current
    /// directory and the server binary), else <c>activity/</c> in the current directory.
    /// </summary>
    public static string DefaultDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("CS1_ACTIVITY_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Cs1Mayor.sln")))
                {
                    return Path.Combine(dir.FullName, "activity");
                }
            }
        }
        return Path.Combine(Environment.CurrentDirectory, "activity");
    }

    /// <summary>Appends one entry (with game context) and returns it.</summary>
    public async Task<JsonObject> WriteAsync(string kind, string? text = null, JsonObject? extra = null,
        CancellationToken cancellationToken = default)
    {
        var game = await GameContextAsync(cancellationToken).ConfigureAwait(false);

        var entry = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _sequence),
            ["wall"] = _clock().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["session"] = SessionId,
            ["kind"] = kind,
        };
        if (!string.IsNullOrWhiteSpace(text))
        {
            entry["text"] = text.Trim();
        }
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                entry[key] = value?.DeepClone();
            }
        }
        entry["game"] = game;

        string line = entry.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n";
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.AppendAllTextAsync(FilePath, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
        return entry;
    }

    /// <summary>The last <paramref name="limit"/> entries of this session.</summary>
    public async Task<JsonArray> ReadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var entries = new JsonArray();
        if (!File.Exists(FilePath))
        {
            return entries;
        }
        string[] lines;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lines = await File.ReadAllLinesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
        foreach (string line in lines.Where(l => l.Length > 0).TakeLast(Math.Max(1, limit)))
        {
            entries.Add(JsonNode.Parse(line));
        }
        return entries;
    }

    /// <summary>Logs one tool call: the tool, its arguments and a compact result.</summary>
    public Task<JsonObject> WriteToolCallAsync(string tool, IReadOnlyDictionary<string, JsonElement>? arguments,
        CallToolResult result, CancellationToken cancellationToken = default)
    {
        string kind = JournalledTools.TryGetValue(tool, out var k) ? k : "tool";
        var args = new JsonObject();
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                args[key] = JsonNode.Parse(value.GetRawText());
            }
        }
        var extra = new JsonObject
        {
            ["tool"] = tool,
            ["args"] = args,
            ["result"] = Summarise(result),
        };
        return WriteAsync(kind, null, extra, cancellationToken);
    }

    /// <summary>Whether a call of <paramref name="tool"/> with these arguments is logged.</summary>
    public static bool ShouldJournal(string tool, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (!JournalledTools.ContainsKey(tool))
        {
            return false;
        }
        if (arguments is not null && arguments.TryGetValue("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True)
        {
            return false;   // planning, not a decision
        }
        if (arguments is not null && arguments.TryGetValue("action", out var action)
            && action.ValueKind == JsonValueKind.String
            && action.GetString()?.Trim().ToLowerInvariant() is "status" or "state" or "list")
        {
            return false;   // a read through an action tool
        }
        if (tool == "city_request")
        {
            return arguments is not null && arguments.TryGetValue("method", out var method)
                   && method.ValueKind == JsonValueKind.String
                   && string.Equals(method.GetString(), "POST", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>
    /// The MCP call-tool filter that logs every <see cref="ShouldJournal"/> call after it
    /// ran. Log failures are swallowed: the log must never break a tool.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> ToolCallFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            string? tool = context.Params?.Name;
            IReadOnlyDictionary<string, JsonElement>? arguments = context.Params?.Arguments is { } args
                ? new Dictionary<string, JsonElement>(args)
                : null;
            if (tool is not null && ShouldJournal(tool, arguments)
                && context.Services?.GetService<ActivityLog>() is { } journal)
            {
                try
                {
                    await journal.WriteToolCallAsync(tool, arguments, result, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // never let the log fail the mayor's action
                }
            }
            return result;
        };
    }

    public static JsonNode? Summarise(CallToolResult result)
    {
        JsonNode? node = null;
        if (result.StructuredContent is { } structured)
        {
            node = JsonNode.Parse(structured.GetRawText());
        }
        else if (result.Content?.OfType<TextContentBlock>().FirstOrDefault() is { } text)
        {
            try
            {
                node = JsonNode.Parse(text.Text);
            }
            catch (JsonException)
            {
                node = JsonValue.Create(text.Text);
            }
        }
        if (result.IsError == true && node is JsonValue)
        {
            return new JsonObject { ["ok"] = false, ["error"] = node.DeepClone() };
        }
        if (node is null || node.ToJsonString().Length <= MaxResultChars)
        {
            return node;
        }
        if (node is not JsonObject obj)
        {
            return JsonValue.Create(node.ToJsonString()[..MaxResultChars] + "...");
        }
        var compact = new JsonObject { ["truncated"] = true };
        foreach (string key in ResultKeys)
        {
            if (obj[key] is { } value)
            {
                compact[key] = value.DeepClone();
            }
        }
        return compact;
    }

    private async Task<JsonNode?> GameContextAsync(CancellationToken cancellationToken)
    {
        if (_bridge is null)
        {
            return null;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_contextTimeout);
            var summary = await _bridge.GetAsync("/state/summary", timeout.Token).ConfigureAwait(false);
            if (summary?["ok"] is JsonValue ok && ok.TryGetValue(out bool isOk) && !isOk)
            {
                return null;
            }
            return new JsonObject
            {
                ["date"] = summary?["gameTime"]?.DeepClone(),
                ["population"] = summary?["population"]?.DeepClone(),
                ["cash"] = summary?["cash"]?.DeepClone(),
                ["paused"] = summary?["simulation"]?["paused"]?.DeepClone(),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
