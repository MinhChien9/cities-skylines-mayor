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
    You interact with a live Cities: Skylines (the 2015 original) city through raw,
    atomic tools. This server adds NO decision logic: every tool relays one HTTP call
    to an in-game bridge mod that executes it on the game's simulation thread, and
    returns the bridge's response as-is. You decide what to do from the stats you read.

    Mechanical facts worth knowing before you act:
    - Reads and writes are separate tools; mutating tool calls never run
      automatically - you call each one deliberately.
    - city_threads is the go/no-go gate: if runsOnSimulationThread is false, tell the
      user and stop; otherwise every write would race the simulation.
    - Zones only exist alongside roads; zone blocks are created by roads.
    - A road crossing is not an intersection unless segments share endpoints
      (city_build_road reuses a node within ~2 units).
    - Placement does not snap to roads and bulldoze does not refund money.
    - All commands support dryRun=true where validation is meaningful.
    - city_request reaches any bridge endpoint without a dedicated tool; the endpoint
      list lives in docs/04-api-reference.md.
    - Demand moves at most +/-2 per simulation step; the clock is the simulation.
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
