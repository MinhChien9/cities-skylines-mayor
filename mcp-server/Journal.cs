using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The decision journal: one JSON line per decision, note or action, the raw material the
/// video scripts are written from (Phase 3). One file per MCP server session:
/// <c>journal/&lt;yyyyMMdd-HHmmss&gt;.jsonl</c> at the repo root (git-ignored).
///
/// Every entry carries the wall time, the recording timecode (when OBS is recording) and
/// the game date, population and cash, so an editor can find the moment in the video
/// and the script can tell the story in numbers. The context reads are best effort with
/// a short timeout: a closed game or OBS never blocks the mayor, the field is just null.
///
/// Entries come from <c>studio_note</c> and, automatically, from <see cref="ToolCallFilter"/>
/// for every call of a tool that changes the city or the recording.
/// </summary>
public sealed class Journal
{
    /// <summary>Tools journalled automatically, and the entry kind each produces.</summary>
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
        ["city_set_simulation_speed"] = "office",
        ["city_transport_line"] = "office",
        ["city_district"] = "office",
        ["city_save"] = "office",
        ["city_request"] = "office",      // only its POSTs, see ShouldJournal
        ["studio_record"] = "studio",
        ["studio_marker"] = "marker",
        ["studio_timelapse"] = "studio",
    };

    private const int MaxResultChars = 2000;
    private static readonly string[] ResultKeys =
        ["ok", "error", "errors", "hint", "cost", "segmentIds", "nodeIds", "buildingId", "position", "snappedTo", "name", "outputPath", "timecode", "count"];

    private readonly BridgeClient? _bridge;
    private readonly ObsClient? _obs;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _contextTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _sequence;

    public Journal(string directory, BridgeClient? bridge, ObsClient? obs,
        Func<DateTimeOffset>? clock = null, TimeSpan? contextTimeout = null)
    {
        Directory = directory;
        _bridge = bridge;
        _obs = obs;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _contextTimeout = contextTimeout ?? TimeSpan.FromSeconds(2);
        SessionId = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    public string Directory { get; }

    public string SessionId { get; }

    public string FilePath => Path.Combine(Directory, SessionId + ".jsonl");

    /// <summary>
    /// The journal folder: <c>$CS1_JOURNAL_DIR</c>, else <c>journal/</c> in the repo root
    /// (the folder holding <c>Cs1Mayor.sln</c>, searched upwards from the current
    /// directory and the server binary), else <c>journal/</c> in the current directory.
    /// </summary>
    public static string DefaultDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("CS1_JOURNAL_DIR");
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
                    return Path.Combine(dir.FullName, "journal");
                }
            }
        }
        return Path.Combine(Environment.CurrentDirectory, "journal");
    }

    /// <summary>Appends one entry (with game and recording context) and returns it.</summary>
    public async Task<JsonObject> WriteAsync(string kind, string? text = null, JsonObject? extra = null,
        CancellationToken cancellationToken = default)
    {
        var game = GameContextAsync(cancellationToken);
        var recording = RecordingContextAsync(cancellationToken);
        await Task.WhenAll(game, recording).ConfigureAwait(false);

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
        entry["recording"] = recording.Result;
        entry["game"] = game.Result;

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

    /// <summary>Journals one tool call: the tool, its arguments and a compact result.</summary>
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
        string? text = tool == "studio_marker" ? (string?)args["name"] : null;
        return WriteAsync(kind, text, extra, cancellationToken);
    }

    /// <summary>Whether a call of <paramref name="tool"/> with these arguments is journalled.</summary>
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
    /// The MCP call-tool filter that journals every <see cref="ShouldJournal"/> call after
    /// it ran. Journal failures are swallowed: the journal must never break a tool.
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
                && context.Services?.GetService<Journal>() is { } journal)
            {
                try
                {
                    await journal.WriteToolCallAsync(tool, arguments, result, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // never let the journal fail the mayor's action
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

    private async Task<JsonNode?> RecordingContextAsync(CancellationToken cancellationToken)
    {
        if (_obs is null)
        {
            return null;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_contextTimeout);
            var status = await _obs.RequestAsync("GetRecordStatus", null, timeout.Token).ConfigureAwait(false);
            bool active = (bool?)status?["outputActive"] ?? false;
            return new JsonObject
            {
                ["active"] = active,
                ["paused"] = (bool?)status?["outputPaused"] ?? false,
                ["timecode"] = active ? (string?)status?["outputTimecode"] : null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
