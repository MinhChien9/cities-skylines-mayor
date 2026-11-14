using Cs1Mayor.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// CS1 Mayor MCP server.
//
// Speaks MCP over stdio to the AI agent (opencode, Claude Code, ...) and translates
// tool calls into HTTP requests against the in-game bridge mod.
//
// IMPORTANT (stdio hygiene): the MCP wire protocol lives on stdout. Nothing except
// protocol messages may ever be written there. All logging goes to stderr.

string bridgeUrl = args.FirstOrDefault(a => !a.StartsWith('-'))
                   ?? Environment.GetEnvironmentVariable("CS1_BRIDGE_URL")
                   ?? "http://127.0.0.1:32123";

var builder = Host.CreateApplicationBuilder(args);

// Route every log to stderr so stdout stays a clean protocol channel.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(new BridgeClient(bridgeUrl));

// The studio (OBS). Connecting is lazy: the server starts fine without OBS.
var studio = StudioSettings.Load();
builder.Services.AddSingleton(studio);
builder.Services.AddSingleton(new ObsClient(studio.ObsUrl, studio.ObsPassword));

const string Instructions = """
    You are the mayor of a live Cities: Skylines (the 2015 original) city. This server is
    your office AND your hands: it reads every stat the game has, pulls every
    non-spatial lever, and builds through the game's own tool code - the same snapping,
    junction-splitting, collision, slope, locked-tile and money checks the player's road
    and building tools apply. You do not need a cursor or screenshots to play.

    The working pattern:
    1. city_overview - the pulse in one call: summary, demand, economy, problems,
       chirps. Never open an in-game info panel to check numbers; read them here.
    2. city_map - LOOK before you build: a semantic top-down image (land shaded by
       height, water, roads, zones, buildings, locked tiles) with world coordinates on
       it. Plan in metres from it; road dead ends are marked as the places to extend.
    3. Decide. Game facts: zones only exist alongside roads; residential level-up is
       land value + education (schools and parks); keep tax at 9% early; demand moves at
       most +/-2 per simulation step; services need power, water and sewage.
    4. Build: city_build_road (any network, incl. Water Pipe and Power Line; returns the
       new segment ids), city_zone_segment (zone along those ids), city_place_building
       (roadside services snap to the road and face it). Use dryRun when unsure; a
       refusal lists the game's own error names and a hint - change the plan, do not
       retry the same coordinates.
    5. Let time pass with city_set_simulation_speed (speed 3), then verify with reads and
       city_map, then city_save.

    - city_threads is the go/no-go gate: if runsOnSimulationThread is false, tell the
      user and stop.
    - Grid habits that work: straight streets on 8 m multiples, parallel streets 80 m
      apart (16 m road + two 32 m zone blocks), junctions at least ~40 m apart.
    - city_camera frames the game camera on a world point (for the viewer, or for a
      screenshot); city_project converts a world point to desktop pixels if a cursor is
      ever needed.
    - city_request GETs any bridge endpoint (docs/04-api-reference.md has the list) and
      POSTs to the endpoints that have a dedicated tool.

    The studio (OBS) records the play for videos: studio_status, studio_record
    (start/stop/pause/resume), studio_marker (a chapter at a story beat: a decision, a
    mistake, a milestone) and studio_screenshot (a real rendered frame of the game).
    """;

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "cs1-mayor",
            Title = "Cities: Skylines 1 Mayor",
            Version = "0.1.0",
        };
        options.ServerInstructions = Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<CityTools>()
    .WithTools<BuildTools>()
    .WithTools<StudioTools>();

await builder.Build().RunAsync();
