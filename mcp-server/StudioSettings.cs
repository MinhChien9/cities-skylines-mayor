using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Where the studio finds OBS. Written by <c>scripts/setup-obs.ps1</c> to the git-ignored
/// <c>studio.local.json</c> at the repo root (it holds the obs-websocket password).
///
/// Lookup order: <c>$CS1_STUDIO_CONFIG</c> (a file path), then <c>studio.local.json</c> in
/// the current directory or any parent, then next to the server binary or any parent of
/// it (the Release build lives under <c>mcp-server/bin/...</c> inside the repo).
/// <c>$OBS_WEBSOCKET_URL</c> and <c>$OBS_WEBSOCKET_PASSWORD</c> override the file.
/// </summary>
public sealed record StudioSettings
{
    public const string FileName = "studio.local.json";

    public string ObsUrl { get; init; } = "ws://127.0.0.1:4455";
    public string? ObsPassword { get; init; }
    public string Scene { get; init; } = "CS1 Mayor";
    public string GameCapture { get; init; } = "Game Capture: Cities";
    public string? RecordDirectory { get; init; }

    /// <summary>The file the settings came from, or null when only defaults/env were used.</summary>
    public string? SourcePath { get; init; }

    public static StudioSettings Load()
    {
        string? path = Environment.GetEnvironmentVariable("CS1_STUDIO_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = FindUpwards(Environment.CurrentDirectory) ?? FindUpwards(AppContext.BaseDirectory);
        }

        var settings = path is not null && File.Exists(path) ? FromFile(path) : new StudioSettings();

        string? url = Environment.GetEnvironmentVariable("OBS_WEBSOCKET_URL");
        string? password = Environment.GetEnvironmentVariable("OBS_WEBSOCKET_PASSWORD");
        return settings with
        {
            ObsUrl = string.IsNullOrWhiteSpace(url) ? settings.ObsUrl : url,
            ObsPassword = string.IsNullOrEmpty(password) ? settings.ObsPassword : password,
        };
    }

    public static StudioSettings FromFile(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var obs = root?["obs"] as JsonObject ?? new JsonObject();
        var defaults = new StudioSettings();
        return new StudioSettings
        {
            ObsUrl = (string?)obs["url"] ?? defaults.ObsUrl,
            ObsPassword = (string?)obs["password"],
            Scene = (string?)obs["scene"] ?? defaults.Scene,
            GameCapture = (string?)obs["gameCapture"] ?? defaults.GameCapture,
            RecordDirectory = (string?)obs["recordDirectory"],
            SourcePath = Path.GetFullPath(path),
        };
    }

    private static string? FindUpwards(string? start)
    {
        for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
