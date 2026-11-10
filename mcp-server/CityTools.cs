using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The tool surface exposed to the AI agent.
///
/// DESIGN NOTES
/// ------------
/// * One tool per bridge endpoint. The tools are deliberately RAW and ATOMIC: a tool
///   relays a single HTTP call and returns whatever the bridge replied. No merging,
///   no orchestration, no goals, no sessions — the agent decides what to do from the
///   stats it reads back. `docs/04-api-reference.md` documents the endpoint each
///   tool relays.
///
/// * `city_request` is the escape hatch: a raw passthrough to ANY bridge endpoint,
///   so an endpoint added to the bridge is agent-reachable before a dedicated tool
///   ships. The dedicated tools exist for discoverability, not for capability.
///
/// * Reads and writes are separate tools so the agent can never accidentally mutate
///   the city when it only wanted information.
/// </summary>
[McpServerToolType]
public sealed class CityTools
{
    private readonly BridgeClient _bridge;

    public CityTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    // ---------------------------------------------------------------- liveness

    [System.ComponentModel.Description("GET /health. Bridge liveness probe. Works before a city is loaded.")]
    [McpServerTool(Name = "city_health", Title = "Bridge Health")]
    public Task<JsonNode?> CityHealth(CancellationToken cancellationToken)
        => Get("/health", cancellationToken);

    [System.ComponentModel.Description("GET /debug/threads. Thread diagnostics. probeThreadId must equal simulationThreadId and must NOT equal mainThreadId; runsOnSimulationThread must be true. pendingJobs should return to 0 after each command.")]
    [McpServerTool(Name = "city_threads", Title = "Thread Diagnostics")]
    public Task<JsonNode?> CityThreads(CancellationToken cancellationToken)
        => Get("/debug/threads", cancellationToken);

    // ------------------------------------------------------------------- reads

    [System.ComponentModel.Description("GET /state/summary. Compact city overview: date, pause/speed, population, cash, happiness, RCI demand, network counts.")]
    [McpServerTool(Name = "city_summary", Title = "City Summary")]
    public Task<JsonNode?> CitySummary(CancellationToken cancellationToken)
        => Get("/state/summary", cancellationToken);

    [System.ComponentModel.Description("GET /state/problems. Active problem icons (no power, no road access, abandoned, traffic...). Each has a position and a type.")]
    [McpServerTool(Name = "city_problems", Title = "City Problems")]
    public async Task<JsonNode?> CityProblems(
        [System.ComponentModel.Description("Max problems to return (1-500, default 200).")] int limit = 200,
        CancellationToken cancellationToken = default)
        => await Get($"/state/problems?limit={Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/demand. RCI demand values (residential, commercial, workplace).")]
    [McpServerTool(Name = "city_demand", Title = "RCI Demand")]
    public Task<JsonNode?> CityDemand(CancellationToken cancellationToken)
        => Get("/state/demand", cancellationToken);

    [System.ComponentModel.Description("GET /state/chirps. Citizen chirper messages, newest first.")]
    [McpServerTool(Name = "city_chirps", Title = "Citizen Chirps")]
    public async Task<JsonNode?> CityChirps(
        [System.ComponentModel.Description("Max chirps to return (1-500, default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
        => await Get($"/state/chirps?limit={Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/zones. Zoning summary per zone type and district.")]
    [McpServerTool(Name = "city_zones", Title = "Zoning Summary")]
    public Task<JsonNode?> CityZones(CancellationToken cancellationToken)
        => Get("/state/zones", cancellationToken);

    [System.ComponentModel.Description("GET /state/economy. Cash, income, expenses, balance, taxMultiplierPercent, per-service tax rates and budgets.")]
    [McpServerTool(Name = "city_economy", Title = "Economy")]
    public Task<JsonNode?> CityEconomy(CancellationToken cancellationToken)
        => Get("/state/economy", cancellationToken);

    [System.ComponentModel.Description("GET /state/facilities. Service facilities (power, water, police, ...) with coverage data.")]
    [McpServerTool(Name = "city_facilities", Title = "Service Facilities")]
    public async Task<JsonNode?> CityFacilities(
        [System.ComponentModel.Description("Max facilities to return (1-500, default 500).")] int limit = 500,
        [System.ComponentModel.Description("Optional service filter, e.g. 'Power', 'PoliceDepartment', 'Education'.")] string? service = null,
        [System.ComponentModel.Description("Include map objects (roads, parks) that host a service (default false).")] bool includeMapObjects = false,
        CancellationToken cancellationToken = default)
    {
        string path = $"/state/facilities?limit={Clamp(limit, 1, 500)}";
        if (!string.IsNullOrWhiteSpace(service))
        {
            path += "&service=" + Uri.EscapeDataString(service);
        }
        if (includeMapObjects)
        {
            path += "&includeMapObjects=true";
        }
        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/growables. Growable zoned buildings (vacant lots the simulation may develop).")]
    [McpServerTool(Name = "city_growables", Title = "Growable Buildings")]
    public async Task<JsonNode?> CityGrowables(
        [System.ComponentModel.Description("Max rows to return (1-1000, default 500).")] int limit = 500,
        [System.ComponentModel.Description("Optional service filter, e.g. 'Residential', 'Commercial'.")] string? service = null,
        CancellationToken cancellationToken = default)
    {
        string path = $"/state/growables?limit={Clamp(limit, 1, 1000)}";
        if (!string.IsNullOrWhiteSpace(service))
        {
            path += "&service=" + Uri.EscapeDataString(service);
        }
        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/networks. Roads, highways, paths and their connectivity.")]
    [McpServerTool(Name = "city_networks", Title = "Networks")]
    public async Task<JsonNode?> CityNetworks(
        [System.ComponentModel.Description("Max rows to return (1-1000, default 500).")] int limit = 500,
        [System.ComponentModel.Description("Optional service filter, e.g. 'Road', 'PublicTransport'.")] string? service = null,
        CancellationToken cancellationToken = default)
    {
        string path = $"/state/networks?limit={Clamp(limit, 1, 1000)}";
        if (!string.IsNullOrWhiteSpace(service))
        {
            path += "&service=" + Uri.EscapeDataString(service);
        }
        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/road-anomalies. Road network defects: near-miss crossings, short stubs, optional dead ends.")]
    [McpServerTool(Name = "city_road_anomalies", Title = "Road Anomalies")]
    public async Task<JsonNode?> CityRoadAnomalies(
        [System.ComponentModel.Description("Max anomalies to return (1-1000, default 200).")] int limit = 200,
        [System.ComponentModel.Description("Distance below which two unconnected road ends count as a near miss (default 16).")] float nearMissDistance = 16,
        [System.ComponentModel.Description("Segment length below which a segment counts as a stub (default 28).")] float shortSegmentLength = 28,
        [System.ComponentModel.Description("Include dead ends (default true; they are usually legitimate).")] bool includeDeadEnds = true,
        CancellationToken cancellationToken = default)
    {
        string path = $"/state/road-anomalies?limit={Clamp(limit, 1, 1000)}" +
            $"&nearMissDistance={nearMissDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&shortSegmentLength={shortSegmentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&includeDeadEnds={(includeDeadEnds ? "true" : "false")}";
        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/building-anomalies. Building placement defects.")]
    [McpServerTool(Name = "city_building_anomalies", Title = "Building Anomalies")]
    public async Task<JsonNode?> CityBuildingAnomalies(
        [System.ComponentModel.Description("Max anomalies to return (1-1000, default 200).")] int limit = 200,
        CancellationToken cancellationToken = default)
        => await Get($"/state/building-anomalies?limit={Clamp(limit, 1, 1000)}", cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/zone-anomalies. Zoning defects: mottled blocks, unzoned holes.")]
    [McpServerTool(Name = "city_zone_anomalies", Title = "Zone Anomalies")]
    public async Task<JsonNode?> CityZoneAnomalies(
        [System.ComponentModel.Description("Max anomalies to return (1-1000, default 200).")] int limit = 200,
        [System.ComponentModel.Description("Minimum minority cells within a block for a mottle report (default 3).")] int minMinorityCells = 3,
        [System.ComponentModel.Description("Minimum unzoned cells within a block for a hole report (default 6).")] int minUnzonedCells = 6,
        [System.ComponentModel.Description("Include unzoned holes (default true).")] bool includeUnzonedHoles = true,
        CancellationToken cancellationToken = default)
    {
        string path = $"/state/zone-anomalies?limit={Clamp(limit, 1, 1000)}" +
            $"&minMinorityCells={Clamp(minMinorityCells, 0, 1000)}" +
            $"&minUnzonedCells={Clamp(minUnzonedCells, 0, 1000)}" +
            $"&includeUnzonedHoles={(includeUnzonedHoles ? "true" : "false")}";
        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/external-connections. Highway, rail and sea links to the map edge.")]
    [McpServerTool(Name = "city_external_connections", Title = "External Connections")]
    public async Task<JsonNode?> CityExternalConnections(
        [System.ComponentModel.Description("Max connections to return (1-500, default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
        => await Get($"/state/external-connections?limit={Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/saves. Saved games on disk.")]
    [McpServerTool(Name = "city_saves", Title = "Save Files")]
    public Task<JsonNode?> CitySaves(CancellationToken cancellationToken)
        => Get("/state/saves", cancellationToken);

    // --------------------------------------------------------- prefab catalogue

    [System.ComponentModel.Description("GET /prefabs/buildings. Available building prefabs and their exact names for city_place_building.")]
    [McpServerTool(Name = "city_building_prefabs", Title = "Building Prefabs")]
    public async Task<JsonNode?> CityBuildingPrefabs(
        [System.ComponentModel.Description("Optional service filter, e.g. 'Education', 'FireDepartment'.")] string? service = null,
        CancellationToken cancellationToken = default)
        => await Get(Query("/prefabs/buildings", "service", service), cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /prefabs/roads. Available road prefabs and their exact names for city_build_road.")]
    [McpServerTool(Name = "city_road_prefabs", Title = "Road Prefabs")]
    public Task<JsonNode?> CityRoadPrefabs(CancellationToken cancellationToken)
        => Get("/prefabs/roads", cancellationToken);

    [System.ComponentModel.Description("GET /prefabs/networks. Available non-road network prefabs (fences, walls, paths...).")]
    [McpServerTool(Name = "city_network_prefabs", Title = "Network Prefabs")]
    public async Task<JsonNode?> CityNetworkPrefabs(
        [System.ComponentModel.Description("Optional service filter.")] string? service = null,
        CancellationToken cancellationToken = default)
        => await Get(Query("/prefabs/networks", "service", service), cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ act

    [System.ComponentModel.Description("POST /commands/build-road. Build a road segment between two world points. Reuses an existing node within ~2 units of each endpoint, so segments that share endpoints form real intersections.")]
    [McpServerTool(Name = "city_build_road", Title = "Build Road")]
    public async Task<JsonNode?> CityBuildRoad(
        [System.ComponentModel.Description("Road prefab name from city_road_prefabs, e.g. 'Basic Road'.")] string roadPrefab,
        [System.ComponentModel.Description("Start point X in world coordinates.")] double startX,
        [System.ComponentModel.Description("Start point Z in world coordinates.")] double startZ,
        [System.ComponentModel.Description("End point X in world coordinates.")] double endX,
        [System.ComponentModel.Description("End point Z in world coordinates.")] double endZ,
        [System.ComponentModel.Description("Optional road name.")] string? name = null,
        [System.ComponentModel.Description("Validate only, do not build (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/build-road", new Dictionary<string, object?>
        {
            ["roadPrefab"] = roadPrefab,
            ["start"] = new { x = startX, z = startZ },
            ["end"] = new { x = endX, z = endZ },
            ["name"] = name ?? "",
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/set-zone. Paint a zone inside a radius around a point. Zone blocks are created by roads; there is nothing to paint where no road exists.")]
    [McpServerTool(Name = "city_zone", Title = "Paint Zone")]
    public async Task<JsonNode?> CityZone(
        [System.ComponentModel.Description("Zone type: ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, Industrial or Office.")] string zone,
        [System.ComponentModel.Description("Center X in world coordinates.")] double centerX,
        [System.ComponentModel.Description("Center Z in world coordinates.")] double centerZ,
        [System.ComponentModel.Description("Radius in world units (default 40).")] double radius = 40,
        [System.ComponentModel.Description("Do not repaint cells that already have a developed building (default true).")] bool preserveOccupied = true,
        [System.ComponentModel.Description("Validate only, do not paint (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-zone", new Dictionary<string, object?>
        {
            ["zone"] = zone,
            ["center"] = new { x = centerX, z = centerZ },
            ["radius"] = radius,
            ["preserveOccupied"] = preserveOccupied,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/place-building. Place a building by prefab name at a world position. Does not snap to roads. Use dryRun first to check cost and fit.")]
    [McpServerTool(Name = "city_place_building", Title = "Place Building")]
    public async Task<JsonNode?> CityPlaceBuilding(
        [System.ComponentModel.Description("Exact prefab name from city_building_prefabs, e.g. 'Water Tower'.")] string buildingPrefab,
        [System.ComponentModel.Description("Position X in world coordinates.")] double x,
        [System.ComponentModel.Description("Position Z in world coordinates.")] double z,
        [System.ComponentModel.Description("Facing angle in degrees (default 0).")] double angleDegrees = 0,
        [System.ComponentModel.Description("Validate only, do not place (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/place-building", new Dictionary<string, object?>
        {
            ["buildingPrefab"] = buildingPrefab,
            ["position"] = new { x, z },
            ["angleDegrees"] = angleDegrees,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/move-building. Relocate a placed building by id.")]
    [McpServerTool(Name = "city_move_building", Title = "Move Building")]
    public async Task<JsonNode?> CityMoveBuilding(
        [System.ComponentModel.Description("Building id.")] int id,
        [System.ComponentModel.Description("New position X in world coordinates.")] double x,
        [System.ComponentModel.Description("New position Z in world coordinates.")] double z,
        [System.ComponentModel.Description("Facing angle in degrees (default 0).")] double angleDegrees = 0,
        [System.ComponentModel.Description("Validate only, do not move (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/move-building", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["position"] = new { x, z },
            ["angleDegrees"] = angleDegrees,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/set-building-active. Activate or deactivate a building.")]
    [McpServerTool(Name = "city_set_building_active", Title = "Set Building Active")]
    public async Task<JsonNode?> CitySetBuildingActive(
        [System.ComponentModel.Description("Building id.")] int id,
        [System.ComponentModel.Description("True to activate, false to deactivate.")] bool active,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-building-active", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["active"] = active,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/bulldoze. Remove a building, road segment or road node by id. Does not refund money.")]
    [McpServerTool(Name = "city_bulldoze", Title = "Bulldoze")]
    public async Task<JsonNode?> CityBulldoze(
        [System.ComponentModel.Description("Entity type: 'building', 'netSegment' or 'netNode'.")] string entityType,
        [System.ComponentModel.Description("Entity id.")] int id,
        [System.ComponentModel.Description("For netSegment: keep the nodes instead of deleting them (default false).")] bool keepNodes = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/bulldoze", new Dictionary<string, object?>
        {
            ["entityType"] = entityType,
            ["id"] = id,
            ["keepNodes"] = keepNodes,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/set-simulation-speed. Set simulation speed (1-3) and/or pause state.")]
    [McpServerTool(Name = "city_set_simulation_speed", Title = "Set Simulation Speed")]
    public async Task<JsonNode?> CitySetSimulationSpeed(
        [System.ComponentModel.Description("Simulation speed 1-3 (default 1).")] int speed = 1,
        [System.ComponentModel.Description("True to pause, false to resume.")] bool paused = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-simulation-speed", new Dictionary<string, object?>
        {
            ["speed"] = speed,
            ["paused"] = paused,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/set-tax-rate. Set a tax rate for one zone service (0-20 percent). Rate changes move demand by up to 5 points per percent above the zone ceiling.")]
    [McpServerTool(Name = "city_set_tax_rate", Title = "Set Tax Rate")]
    public async Task<JsonNode?> CitySetTaxRate(
        [System.ComponentModel.Description("Service: Residential, Commercial, Industrial or Office.")] string service,
        [System.ComponentModel.Description("Tax rate 0-20.")] int rate,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-tax-rate", new Dictionary<string, object?>
        {
            ["service"] = service,
            ["rate"] = rate,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/save. Save the city to a named slot (.crp on disk).")]
    [McpServerTool(Name = "city_save", Title = "Save City")]
    public async Task<JsonNode?> CitySave(
        [System.ComponentModel.Description("Save name, e.g. 'MyCity'.")] string name,
        CancellationToken cancellationToken = default)
        => await Post("/commands/save", new Dictionary<string, object?>
        {
            ["name"] = name,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/repair-zones-to-growables. Repaint zone cells to match each block's growable building type.")]
    [McpServerTool(Name = "city_repair_zones_to_growables", Title = "Repair Zones To Growables")]
    public async Task<JsonNode?> CityRepairZonesToGrowables(
        [System.ComponentModel.Description("Validate only, do not repair (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/repair-zones-to-growables", new Dictionary<string, object?>
        {
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/repair-zone-clusters. Repair mottled multi-zone blocks into coherent clusters.")]
    [McpServerTool(Name = "city_repair_zone_clusters", Title = "Repair Zone Clusters")]
    public async Task<JsonNode?> CityRepairZoneClusters(
        [System.ComponentModel.Description("Prefer each cluster's growable zone when choosing the repaint (default true).")] bool preferGrowableZone = true,
        [System.ComponentModel.Description("Validate only, do not repair (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/repair-zone-clusters", new Dictionary<string, object?>
        {
            ["preferGrowableZone"] = preferGrowableZone,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/disable-blocked-assets. Disable the broken 'Block Services - ...' asset family so they stop spawning permanently.")]
    [McpServerTool(Name = "city_disable_blocked_assets", Title = "Disable Blocked Assets")]
    public Task<JsonNode?> CityDisableBlockedAssets(CancellationToken cancellationToken)
        => Post("/commands/disable-blocked-assets", new Dictionary<string, object?>(), cancellationToken);

    [System.ComponentModel.Description("POST /commands/batch. Run up to 32 commands in sequence on one simulation tick. Each command is a JSON object with a 'type' ('build-road' or 'set-zone') plus its fields. Missing dryRun defaults to true unless dryRun=false is passed. stopOnError defaults to true.")]
    [McpServerTool(Name = "city_batch", Title = "Batch Commands")]
    public async Task<JsonNode?> CityBatch(
        [System.ComponentModel.Description("JSON array of command objects, e.g. '[{\"type\":\"build-road\",\"roadPrefab\":\"Basic Road\",\"start\":{\"x\":1,\"z\":2},\"end\":{\"x\":3,\"z\":4}}]'. Each also accepts \"type\":\"set-zone\".")] string commandsJson,
        [System.ComponentModel.Description("Stop the batch at the first failure (default true).")] bool stopOnError = true,
        [System.ComponentModel.Description("Default dryRun for commands that do not set their own (default true).")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var commands = JsonNode.Parse(commandsJson);
        return await Post("/commands/batch", new Dictionary<string, object?>
        {
            ["commands"] = commands,
            ["stopOnError"] = stopOnError,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- passthrough

    [System.ComponentModel.Description("Raw passthrough to ANY bridge endpoint, for cases where no dedicated tool exists (e.g. the POST /commands/build-network alias of build-road, or an endpoint added after this server was built). Endpoint list: docs/04-api-reference.md. Returns the bridge response as-is.")]
    [McpServerTool(Name = "city_request", Title = "Raw Bridge Request")]
    public async Task<JsonNode?> CityRequest(
        [System.ComponentModel.Description("HTTP method: 'GET' or 'POST'.")] string method,
        [System.ComponentModel.Description("Path starting with '/', e.g. '/state/summary' or '/commands/build-network'. Query string allowed on GET.")] string path,
        [System.ComponentModel.Description("JSON body string for POST, e.g. '{\"speed\":2,\"paused\":false}'. Ignored on GET.")] string? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        string normalized = (method ?? "").Trim().ToUpperInvariant();
        if (normalized is not ("GET" or "POST"))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"method must be GET or POST, got '{method}'",
            };
        }
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "path must start with '/'",
            };
        }

        if (normalized == "POST")
        {
            JsonNode? body = string.IsNullOrWhiteSpace(jsonBody) ? new JsonObject() : JsonNode.Parse(jsonBody);
            return await Post(path, body, cancellationToken).ConfigureAwait(false);
        }

        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- internals

    private async Task<JsonNode?> Get(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _bridge.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return Error(ex);
        }
    }

    private async Task<JsonNode?> Post(string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await _bridge.PostAsync(path, body, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return Error(ex);
        }
    }

    /// <summary>
    /// Converts a transport failure into a structured, agent-readable error instead of
    /// an exception the agent cannot act on.
    /// </summary>
    private static JsonNode? Error(BridgeException ex)
    {
        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = ex.Message,
            ["hint"] = "Start Cities: Skylines with the CS1 Mayor Bridge mod enabled and load a city, then retry. See city_health.",
        };
    }

    private static string Query(string path, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return path;
        }
        return path + "?" + key + "=" + Uri.EscapeDataString(value);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
}
