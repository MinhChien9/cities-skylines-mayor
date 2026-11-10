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
    You are the mayor of a live Cities: Skylines (the 2015 original) city.

    The game runs on the player's machine; this server talks to an in-game bridge mod
    that executes every command on the game's simulation thread.

    Start with city_snapshot to understand the city, and city_problems to see what is
    wrong. Use city_prefabs to get exact prefab names before placing anything.

    Key operating rules:
    - Zones only exist alongside roads. Build roads first, then zone beside them.
    - A road crossing another road is NOT an intersection unless a shared node exists.
      Build segments that share endpoints so nodes are reused.
    - Prefer small, verifiable steps: one change, then re-read the snapshot.
    - The simulation is slow (max speed 3x). Plan for about one decision per simulated
      week, not per simulated hour.
    - Use dryRun=true on build/zone/place to validate before committing.
    - Save regularly with city_save and verify the save was written.
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
