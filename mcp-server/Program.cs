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

const string Instructions = """
    You are the mayor of a live Cities: Skylines (the 2015 original) city. This server is
    your OFFICE: it sees everything and controls everything that needs no spatial
    judgement, but it never builds anything. Physical work - roads, zones, buildings,
    bulldozing, transit stops, district painting - is done with your cursor through the
    game's own UI (your CUA channel), where snapping, validation and visual judgement
    live. Building those things blind, by coordinate, always produced bad results, so
    the build tools are gone on purpose. Do not try to rebuild them through
    city_request; its POST allowlist refuses them.

    The working pattern:
    1. city_overview - the pulse in one call: summary, demand, economy, problems,
       chirps. Never open an in-game info panel to check numbers; read them here.
    2. Decide. Game facts: zones only exist alongside roads; residential level-up is
       land value + education (schools and parks, not more fire stations); keep tax
       at 9% early; demand moves at most +/-2 per simulation step.
    3. Pause or speed the clock with city_set_simulation_speed while you plan or while
       the cursor works. The clock is the simulation - changes need simulated time.
    4. Act: cursor for spatial work (via CUA), office tools for policies, taxes,
       budgets, line and district management, save.
    5. Verify with reads (city_overview or the dedicated one), then city_save.

    - city_threads is the go/no-go gate: if runsOnSimulationThread is false, tell the
      user and stop.
    - Aiming the cursor: city_camera frames the camera on a world point (from
      city_problems or an anomaly read), then city_project turns a world point into
      desktop pixels for the CUA click. Re-project after every camera move.
    - city_request GETs any bridge endpoint (docs/04-api-reference.md has the list);
      POST only reaches the office allowlist (policies, budgets, taxes, pause, save,
      line/district management, building active, camera, projection).
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
    .WithTools<CityTools>();

await builder.Build().RunAsync();
