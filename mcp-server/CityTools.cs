using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The tool surface exposed to the AI agent.
///
/// DESIGN NOTES
/// ------------
/// * The tools are deliberately COARSE and action-shaped rather than one-per-endpoint.
///   opencode runs MCP tools through Code Mode, where a namespace listing costs one line
///   of context regardless of how many tools a server has. The real cost of many tools is
///   retrieval precision: the agent has to pick the right one out of a long list of
///   near-identical descriptions. Eight well-named tools beat forty fuzzy ones.
///
/// * Every tool that can return a lot of data takes an explicit `limit`, and the
///   underlying bridge already aggregates. A full 10,000-citizen JSON dump is ~300,000
///   tokens; the aggregated summary is ~118 tokens. The bridge is the right place to
///   aggregate because it is the only place with access to the raw simulation state.
///
/// * Reads and writes are separate tools so the agent can never accidentally mutate the
///   city when it only wanted information.
/// </summary>
[McpServerToolType]
public sealed class CityTools
{
    private readonly BridgeClient _bridge;

    public CityTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    // ---------------------------------------------------------------- observe

    [System.ComponentModel.Description("Read a compact summary of the whole city: date, population, money, demand, happiness, services, problems. Start here.")]
    [McpServerTool(Name = "city_snapshot", Title = "City Snapshot")]
    public async Task<JsonNode?> CitySnapshot(CancellationToken cancellationToken)
    {
        return await Guard(async () =>
        {
            // /state/summary is already the aggregated view. Combine it with the demand and
            // economy views because an agent almost always needs all three together.
            var summary = await _bridge.GetAsync("/state/summary", cancellationToken).ConfigureAwait(false);
            var demand = await _bridge.GetAsync("/state/demand", cancellationToken).ConfigureAwait(false);
            var economy = await _bridge.GetAsync("/state/economy", cancellationToken).ConfigureAwait(false);

            return Merge(("summary", summary), ("demand", demand), ("economy", economy));
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("List active problem icons (no power, no road access, abandoned, traffic, crime...). Each has a position and a count.")]
    [McpServerTool(Name = "city_problems", Title = "City Problems")]
    public async Task<JsonNode?> CityProblems(
        [System.ComponentModel.Description("Max problems to return (default 40, max 200).")] int limit = 40,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var problems = await _bridge.GetAsync($"/state/problems?limit={Clamp(limit, 1, 200)}", cancellationToken).ConfigureAwait(false);
            return problems;
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Read district list with population and specialization, zoning occupancy, and available growable land.")]
    [McpServerTool(Name = "city_districts", Title = "Districts And Zones")]
    public async Task<JsonNode?> CityDistricts(CancellationToken cancellationToken)
    {
        return await Guard(async () =>
        {
            var zones = await _bridge.GetAsync("/state/zones", cancellationToken).ConfigureAwait(false);
            var growables = await _bridge.GetAsync("/state/growables?limit=300", cancellationToken).ConfigureAwait(false);
            return Merge(("zones", zones), ("growables", growables));
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Audit roads and buildings for defects: disconnected crossings, short stubs, overlaps, orphaned service coverage.")]
    [McpServerTool(Name = "city_infrastructure", Title = "Infrastructure Audit")]
    public async Task<JsonNode?> CityInfrastructure(CancellationToken cancellationToken)
    {
        return await Guard(async () =>
        {
            var roads = await _bridge.GetAsync("/state/road-anomalies?limit=100&nearMissDistance=18&shortSegmentLength=32&includeDeadEnds=false", cancellationToken).ConfigureAwait(false);
            var buildings = await _bridge.GetAsync("/state/building-anomalies?limit=100", cancellationToken).ConfigureAwait(false);
            var zones = await _bridge.GetAsync("/state/zone-anomalies?limit=100&includeUnzonedHoles=true", cancellationToken).ConfigureAwait(false);
            return Merge(("roads", roads), ("buildings", buildings), ("zones", zones));
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Read service facilities and their coverage: power, water, sewage, garbage, fire, police, healthcare, education, parks, transport.")]
    [McpServerTool(Name = "city_services", Title = "Service Coverage")]
    public async Task<JsonNode?> CityServices(
        [System.ComponentModel.Description("Optional service filter, e.g. 'Power', 'PoliceDepartment', 'Education'.")] string? service = null,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            string path = "/state/facilities?limit=200";
            if (!string.IsNullOrWhiteSpace(service))
            {
                path += "&service=" + Uri.EscapeDataString(service);
            }
            return await _bridge.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("List the buildings, roads and networks available in the running game (exact prefab names for placement).")]
    [McpServerTool(Name = "city_prefabs", Title = "Available Prefabs")]
    public async Task<JsonNode?> CityPrefabs(
        [System.ComponentModel.Description("What to list: 'buildings', 'roads' or 'networks' (default 'buildings').")] string kind = "buildings",
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            string path = kind.ToLowerInvariant() switch
            {
                "roads" => "/prefabs/roads",
                "networks" => "/prefabs/networks",
                _ => "/prefabs/buildings",
            };
            return await _bridge.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- act

    [System.ComponentModel.Description("Build a road segment between two world points. Reuses an existing node when one is nearby, so crossings connect. Prefer building one segment at a time.")]
    [McpServerTool(Name = "city_build_road", Title = "Build Road")]
    public async Task<JsonNode?> CityBuildRoad(
        [System.ComponentModel.Description("Road prefab name, e.g. 'Basic Road', 'Basic Avenue', 'Highway One-Lane'.")] string roadPrefab,
        [System.ComponentModel.Description("Start point X in world coordinates.")] double startX,
        [System.ComponentModel.Description("Start point Z in world coordinates.")] double startZ,
        [System.ComponentModel.Description("End point X in world coordinates.")] double endX,
        [System.ComponentModel.Description("End point Z in world coordinates.")] double endZ,
        [System.ComponentModel.Description("Optional road name.")] string? name = null,
        [System.ComponentModel.Description("Validate only, do not build.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var body = new Dictionary<string, object?>
            {
                ["roadPrefab"] = roadPrefab,
                ["start"] = new { x = startX, z = startZ },
                ["end"] = new { x = endX, z = endZ },
                ["name"] = name ?? "",
                ["dryRun"] = dryRun,
            };
            return await _bridge.PostAsync("/commands/build-road", body, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Paint a zone (ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, Industrial, Office) inside a radius. Zones only exist alongside roads.")]
    [McpServerTool(Name = "city_zone", Title = "Paint Zone")]
    public async Task<JsonNode?> CityZone(
        [System.ComponentModel.Description("Zone type: ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, Industrial or Office.")] string zone,
        [System.ComponentModel.Description("Center X in world coordinates.")] double centerX,
        [System.ComponentModel.Description("Center Z in world coordinates.")] double centerZ,
        [System.ComponentModel.Description("Radius in world units (default 40).")] double radius = 40,
        [System.ComponentModel.Description("Do not repaint cells that already have a developed building (default true).")] bool preserveOccupied = true,
        [System.ComponentModel.Description("Validate only, do not paint.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var body = new Dictionary<string, object?>
            {
                ["zone"] = zone,
                ["center"] = new { x = centerX, z = centerZ },
                ["radius"] = radius,
                ["preserveOccupied"] = preserveOccupied,
                ["dryRun"] = dryRun,
            };
            return await _bridge.PostAsync("/commands/set-zone", body, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Place a building by prefab name at a world position. Use city_prefabs to get exact prefab names.")]
    [McpServerTool(Name = "city_place_building", Title = "Place Building")]
    public async Task<JsonNode?> CityPlaceBuilding(
        [System.ComponentModel.Description("Exact prefab name from city_prefabs, e.g. 'Small Residential Home'.")] string buildingPrefab,
        [System.ComponentModel.Description("Position X in world coordinates.")] double x,
        [System.ComponentModel.Description("Position Z in world coordinates.")] double z,
        [System.ComponentModel.Description("Facing angle in degrees (0 = default).")] double angleDegrees = 0,
        [System.ComponentModel.Description("Validate only, do not place.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var body = new Dictionary<string, object?>
            {
                ["buildingPrefab"] = buildingPrefab,
                ["position"] = new { x, z },
                ["angleDegrees"] = angleDegrees,
                ["dryRun"] = dryRun,
            };
            return await _bridge.PostAsync("/commands/place-building", body, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Remove a building, road segment or road node by id. Use city_infrastructure to find ids. This does not refund money.")]
    [McpServerTool(Name = "city_bulldoze", Title = "Bulldoze")]
    public async Task<JsonNode?> CityBulldoze(
        [System.ComponentModel.Description("Entity type: 'building', 'netSegment' or 'netNode'.")] string entityType,
        [System.ComponentModel.Description("Entity id.")] int id,
        [System.ComponentModel.Description("For netSegment: keep the nodes instead of deleting them.")] bool keepNodes = false,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var body = new Dictionary<string, object?>
            {
                ["entityType"] = entityType,
                ["id"] = id,
                ["keepNodes"] = keepNodes,
            };
            return await _bridge.PostAsync("/commands/bulldoze", body, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Read economy detail and set tax rates, service budgets, and simulation speed/pause.")]
    [McpServerTool(Name = "city_economy", Title = "Economy And Time")]
    public async Task<JsonNode?> CityEconomy(
        [System.ComponentModel.Description("Set a tax rate: service (Residential/Commercial/Industrial/Office), rate 0-20.")] string? setTaxService = null,
        [System.ComponentModel.Description("Tax rate 0-20 when setTaxService is given.")] int? setTaxRate = null,
        [System.ComponentModel.Description("Simulation speed 1-3, or 0 to pause.")] int? setSpeed = null,
        [System.ComponentModel.Description("True to pause the simulation, false to resume.")] bool? setPaused = null,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            if (!string.IsNullOrWhiteSpace(setTaxService) && setTaxRate.HasValue)
            {
                var taxBody = new Dictionary<string, object?>
                {
                    ["service"] = setTaxService,
                    ["rate"] = setTaxRate.Value,
                };
                await _bridge.PostAsync("/commands/set-tax-rate", taxBody, cancellationToken).ConfigureAwait(false);
            }

            if (setSpeed.HasValue || setPaused.HasValue)
            {
                var speedBody = new Dictionary<string, object?>
                {
                    ["speed"] = setSpeed ?? 1,
                    ["paused"] = setPaused ?? false,
                };
                await _bridge.PostAsync("/commands/set-simulation-speed", speedBody, cancellationToken).ConfigureAwait(false);
            }

            return await _bridge.GetAsync("/state/economy", cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Save the city to a named slot and verify the save file was written.")]
    [McpServerTool(Name = "city_save", Title = "Save City")]
    public async Task<JsonNode?> CitySave(
        [System.ComponentModel.Description("Save name, e.g. 'AgentAutoSave'.")] string name,
        CancellationToken cancellationToken = default)
    {
        return await Guard(async () =>
        {
            var body = new Dictionary<string, object?> { ["name"] = name };
            var result = await _bridge.PostAsync("/commands/save", body, cancellationToken).ConfigureAwait(false);
            var saves = await _bridge.GetAsync("/state/saves", cancellationToken).ConfigureAwait(false);
            return Merge(("save", result), ("saves", saves));
        }).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("Check that the in-game bridge is alive and confirm which thread executes commands. Use to diagnose a stuck or mis-threaded bridge.")]
    [McpServerTool(Name = "city_health", Title = "Bridge Health")]
    public async Task<JsonNode?> CityHealth(CancellationToken cancellationToken)
    {
        return await Guard(async () =>
        {
            var health = await _bridge.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            var threads = await _bridge.GetAsync("/debug/threads", cancellationToken).ConfigureAwait(false);
            return Merge(("health", health), ("threads", threads));
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- internals

    /// <summary>
    /// Runs a bridge call and converts a transport failure into a structured, agent-readable
    /// error instead of an exception the agent cannot act on.
    /// </summary>
    private async Task<JsonNode?> Guard(Func<Task<JsonNode?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message,
                ["hint"] = "Start Cities: Skylines with the SkylinesAgentBridge mod enabled and load a city, then retry.",
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Combines several bridge responses into one object, each under its own key.
    ///
    /// Nesting (rather than flattening) matters: several bridge payloads start with the
    /// same keys ("ok", "simulation", ...), so a flat merge would silently drop all but
    /// the last one. It also keeps the agent's context small by returning one payload
    /// instead of three.
    /// </summary>
    private static JsonNode? Merge(params (string Key, JsonNode? Value)[] parts)
    {
        var merged = new JsonObject();
        foreach (var (key, value) in parts)
        {
            merged[key] = value?.DeepClone();
        }
        return merged;
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
}
