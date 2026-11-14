using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The mayor's side of the decision journal (<see cref="Journal"/>): write a note in the
/// mayor's own words, and read the story so far back. Build and office actions are
/// journalled automatically; the notes are the *why* that the scripts need.
/// </summary>
[McpServerToolType]
public sealed class JournalTools
{
    public static readonly string[] Kinds = ["decision", "mistake", "milestone", "observation", "plan", "note"];

    private readonly Journal _journal;

    public JournalTools(Journal journal)
    {
        _journal = journal;
    }

    [Description("Write to the decision journal - the raw material for the video scripts. Build, office and recording actions are journalled automatically with their numbers; a note adds the WHY in your own words: what you saw, what you decided and why, what went wrong, what you expect. One or two plain sentences, written for a viewer. kind: decision (a choice and its reason), mistake (something that went wrong, and what you learned), milestone (population, a first, an unlock), observation (what the numbers or the map show), plan (what you will do next), note. Each entry is stamped with the time, the recording timecode and the game date, population and cash.")]
    [McpServerTool(Name = "studio_note", Title = "Journal Note")]
    public async Task<JsonNode?> StudioNote(
        [Description("The note, one or two sentences.")] string text,
        [Description("decision, mistake, milestone, observation, plan or note (default).")] string kind = "note",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject { ["ok"] = false, ["error"] = "A note needs text." };
        }
        string normalized = kind.Trim().ToLowerInvariant();
        if (!Kinds.Contains(normalized))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"Unknown kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.",
            };
        }
        var entry = await _journal.WriteAsync(normalized, text, null, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["ok"] = true, ["file"] = _journal.FilePath, ["entry"] = entry };
    }

    [Description("Read back the newest entries of this session's decision journal (notes and the automatic build/office/recording entries). Use it to recall the story so far, e.g. after a context reset, before writing a marker or a note.")]
    [McpServerTool(Name = "studio_journal", Title = "Journal (recent)", ReadOnly = true)]
    public async Task<JsonNode?> StudioJournal(
        [Description("How many of the newest entries (1-200, default 20).")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadRecentAsync(Math.Clamp(limit, 1, 200), cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["session"] = _journal.SessionId,
            ["file"] = _journal.FilePath,
            ["entries"] = entries,
        };
    }
}
