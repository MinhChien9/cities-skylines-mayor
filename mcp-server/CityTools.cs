using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The office half of the tool surface: every read, and every lever that needs no
/// spatial judgement (clock, taxes, budgets, policies, saves, line and district
/// management, camera).
///
///   * READS  — every stat the game has, so the agent never has to open an
///              in-game info panel. `city_overview` is one call for the pulse.
///   * OFFICE — pause/speed, taxes, budgets, policies, save, line management,
///              district naming/specialising.
///   * `city_request` — raw passthrough: GET anything, POST to any endpoint that has a
///              dedicated tool (the allowlist below).
///
/// Spatial work — seeing the land and building on it — lives in <see cref="BuildTools"/>.
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

    [System.ComponentModel.Description("THE PULSE — one call instead of five panel reads: date, pause/speed, population, cash, happiness, RCI demand, network counts, the economy (income/expenses/tax multiplier), the current problem icons, and the newest citizen chirps. Open with this every turn; drill into the dedicated reads only when a number looks wrong.")]
    [McpServerTool(Name = "city_overview", Title = "City Overview")]
    public async Task<JsonNode?> CityOverview(
        [System.ComponentModel.Description("Max problem icons to include (1-500, default 60).")] int problemLimit = 60,
        [System.ComponentModel.Description("Max chirps to include (1-100, default 15).")] int chirpLimit = 15,
        CancellationToken cancellationToken = default)
    {
        var summary = Get("/state/summary", cancellationToken);
        var demand = Get("/state/demand", cancellationToken);
        var economy = Get("/state/economy", cancellationToken);
        var problems = Get($"/state/problems?limit={Clamp(problemLimit, 1, 500)}", cancellationToken);
        var chirps = Get($"/state/chirps?limit={Clamp(chirpLimit, 1, 100)}", cancellationToken);
        await Task.WhenAll(summary, demand, economy, problems, chirps).ConfigureAwait(false);

        return new JsonObject
        {
            ["summary"] = summary.Result,
            ["demand"] = demand.Result,
            ["economy"] = economy.Result,
            ["problems"] = problems.Result,
            ["chirps"] = chirps.Result,
        };
    }

    [System.ComponentModel.Description("GET /state/capabilities. What the running game supports: version, DLC, milestone list, current milestone, unlocked/locked features, the enabled mods and which are cheat mods (cheatMods: UnlimitedMoney, UnlockAll...), loadedContent (building prefab counts per service/sub-service, each expansion loaded true/false with evidence, and missingContent: check a scenario's goals against it before playing), and the IThreading simulation clock (simulationTime/frame/tick) — compare two reads to know how much simulated time actually elapsed.")]
    [McpServerTool(Name = "city_capabilities", Title = "Game Capabilities")]
    public Task<JsonNode?> CityCapabilities(CancellationToken cancellationToken)
        => Get("/state/capabilities", cancellationToken);

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

    [System.ComponentModel.Description("GET /state/demand. RCI demand values (residential, commercial, workplace) and, in formula, why: the smoothed demand vs the tax/policy offset (published = actual + offset), occupied/empty homes, total/vacant jobs, unemployed and homeless citizens, abandoned buildings. Read it when growth stalls.")]
    [McpServerTool(Name = "city_demand", Title = "RCI Demand")]
    public Task<JsonNode?> CityDemand(CancellationToken cancellationToken)
        => Get("/state/demand", cancellationToken);

    [System.ComponentModel.Description("GET /state/scenario. In a scenario: every win and lose trigger with each condition's live progress (description, progress text, current/min/max, passed), as the in-game Scenario goals tab shows them. isScenario false in free play.")]
    [McpServerTool(Name = "city_scenario", Title = "Scenario Goals")]
    public Task<JsonNode?> CityScenario(CancellationToken cancellationToken)
        => Get("/state/scenario", cancellationToken);

    [System.ComponentModel.Description("GET /state/areas. The map tiles (5 x 5 grid of 1920 m): which the city owns, which it can buy now (canUnlock: touches an owned tile and milestones allow another), the price and the world bounds of each. areaCount / maxAreaCount say how many are owned / allowed.")]
    [McpServerTool(Name = "city_areas", Title = "Map Tiles")]
    public Task<JsonNode?> CityAreas(CancellationToken cancellationToken)
        => Get("/state/areas", cancellationToken);

    [System.ComponentModel.Description("POST /commands/unlock-area. Buy a map tile as the game's area tool does (pays the land price, then unlocks it). Give the tile's grid x/z from city_areas, or a world point inside it. Refused when it does not touch an owned tile, the milestone limit is reached or the cash is short. dryRun to check.")]
    [McpServerTool(Name = "city_unlock_area", Title = "Buy Map Tile")]
    public async Task<JsonNode?> CityUnlockArea(
        [System.ComponentModel.Description("Tile grid x (0-4) from city_areas; or -1 and give worldX/worldZ.")] int x = -1,
        [System.ComponentModel.Description("Tile grid z (0-4) from city_areas.")] int z = -1,
        [System.ComponentModel.Description("A world x inside the tile (instead of grid x/z).")] double? worldX = null,
        [System.ComponentModel.Description("A world z inside the tile.")] double? worldZ = null,
        [System.ComponentModel.Description("Validate only (price, adjacency, money).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["dryRun"] = dryRun };
        if (worldX.HasValue && worldZ.HasValue)
        {
            body["position"] = new Dictionary<string, object?> { ["x"] = worldX.Value, ["z"] = worldZ.Value };
        }
        else
        {
            body["x"] = x;
            body["z"] = z;
        }
        return await Post("/commands/unlock-area", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("GET /state/industry. Is the goods chain flowing? Factories' input stock (raw materials) and output stock (goods) against the game's own capacities, shops' goods stock, the supply problems (NoResources, NoPlaceforGoods, NoGoods, NoWorkers), industryPerShop, levelUpServices (each factory's service score with the game's formula: under 30 = level 1, under 60 = level 2; cargo coverage counts 5x police; factoriesWithoutCargo), a short diagnosis, and the buildings in trouble worst first (id, position, input/output material, amount, max, percent). Read it when shops report NoGoods or factories NoResources, and before rezoning industry to offices.")]
    [McpServerTool(Name = "city_industry", Title = "Industry & Goods Chain")]
    public async Task<JsonNode?> CityIndustry(
        [System.ComponentModel.Description("Maximum number of troubled buildings to list (default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
        => await Get("/state/industry?limit=" + Clamp(limit, 0, 1000), cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/resources. Natural resources per map tile: for ore, oil, forest and fertility the tile mean (0-255), richKm2 (cells at 128+) and richCentre (where to zone that specialised industry), plus ownedRichKm2 over the owned tiles. A Forest/Farming/Oil/Ore district only produces where its buildings stand on the resource. city_map overlay=ore|oil|forest|fertility shows the grid.")]
    [McpServerTool(Name = "city_resources", Title = "Natural Resources")]
    public Task<JsonNode?> CityResources(CancellationToken cancellationToken)
        => Get("/state/resources", cancellationToken);

    [System.ComponentModel.Description("GET /state/utilities. City-wide capacity against usage for every capacity-limited service: electricity (kW), water, sewage, garbage, healthcare, deathcare, education (capacity vs need per level), crime, jobs and unemployment. Check it before growing the city: a negative surplus is the next thing to build.")]
    [McpServerTool(Name = "city_utilities", Title = "Utility Capacity")]
    public Task<JsonNode?> CityUtilities(CancellationToken cancellationToken)
        => Get("/state/utilities", cancellationToken);

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

    [System.ComponentModel.Description("GET /state/budgets. Per-service day/night budget percentages (100 = baseline spending). Day is 05:00-20:00 and night is a separate value. The 101% trick buys one extra vehicle per service building for ~1% more money.")]
    [McpServerTool(Name = "city_budgets", Title = "Service Budgets")]
    public Task<JsonNode?> CityBudgets(CancellationToken cancellationToken)
        => Get("/state/budgets", cancellationToken);

    [System.ComponentModel.Description("GET /state/policies. The full DistrictPolicies.Policies catalogue (127 values): name, game group (Services/Taxation/CityPlanning/Park/Event/Specialization), whether each is loaded yet, and current state city-wide plus per district. Use these exact names with city_set_policy.")]
    [McpServerTool(Name = "city_policies", Title = "Policy Catalogue")]
    public Task<JsonNode?> CityPolicies(CancellationToken cancellationToken)
        => Get("/state/policies", cancellationToken);

    [System.ComponentModel.Description("GET /state/transport. All transport lines: id, name, type, complete flag, day/night active, stop count and stop positions, vehicle count and selected vehicle, and per-line passenger counters. Ridership needs simulated time to accumulate — compare two reads after letting time run.")]
    [McpServerTool(Name = "city_transport", Title = "Transport Lines")]
    public async Task<JsonNode?> CityTransport(
        [System.ComponentModel.Description("Max lines to return (1-251, default 251 — the game's own line limit).")] int limit = 251,
        CancellationToken cancellationToken = default)
        => await Get($"/state/transport?limit={Clamp(limit, 1, 251)}", cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("GET /state/districts. Districts and special areas. Districts have an id, name, area in cells, population, happiness and specialization; special areas (park, industry, campus, airport, pedestrian zone, ...) have a parkType, parkLevel, group, main gate and whether they are functional. Also reports the 512x512 grid geometry (19.2 m cells) and how many cells are painted.")]
    [McpServerTool(Name = "city_districts", Title = "Districts & Areas")]
    public Task<JsonNode?> CityDistricts(CancellationToken cancellationToken)
        => Get("/state/districts", cancellationToken);

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

    [System.ComponentModel.Description("GET /state/maps. The maps and scenarios a new game can start from (name, displayName, environment, asset). Works at the main menu. Feed a name to city_new_game.")]
    [McpServerTool(Name = "city_maps", Title = "Maps & Scenarios")]
    public Task<JsonNode?> CityMaps(CancellationToken cancellationToken)
        => Get("/state/maps", cancellationToken);

    [System.ComponentModel.Description("POST /commands/load. Load a save by name (from city_saves), the way the game's Load panel does - from the main menu or replacing the city that is running (save that one first with city_save). Waits until the level has loaded (up to waitSeconds) and returns the summary, so you can verify that cityName and population match the save. Works before any city is loaded.")]
    [McpServerTool(Name = "city_load_game", Title = "Load Save")]
    public async Task<JsonNode?> CityLoadGame(
        [System.ComponentModel.Description("The save's name as city_saves lists it.")] string name,
        [System.ComponentModel.Description("Seconds to wait for the level to load (default 240; 0 = return at once).")] int waitSeconds = 240,
        [System.ComponentModel.Description("Only check that the save exists.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["dryRun"] = dryRun };
        JsonNode? start = await Post("/commands/load", body, cancellationToken).ConfigureAwait(false);
        return await WaitForLoad(start, dryRun ? 0 : waitSeconds, start?["cityName"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/new-game. Start a new game from a map or a scenario (names from city_maps), the way the game's New Game / New Scenario panels do - from the main menu or replacing the running city (save it first). Give exactly one of map or scenario. Waits until the level has loaded and returns the summary; for a scenario, read city_scenario next.")]
    [McpServerTool(Name = "city_new_game", Title = "New Game")]
    public async Task<JsonNode?> CityNewGame(
        [System.ComponentModel.Description("Map name or display name (city_maps.maps).")] string? map = null,
        [System.ComponentModel.Description("Scenario name or display name (city_maps.scenarios), e.g. 'City of Gardens'.")] string? scenario = null,
        [System.ComponentModel.Description("The new city's name (default: the map's or scenario's name).")] string? cityName = null,
        [System.ComponentModel.Description("Left-hand traffic (default false: right-hand).")] bool leftHandTraffic = false,
        [System.ComponentModel.Description("Seconds to wait for the level to load (default 240; 0 = return at once).")] int waitSeconds = 240,
        [System.ComponentModel.Description("Only check that the map or scenario exists.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["leftHandTraffic"] = leftHandTraffic, ["dryRun"] = dryRun };
        if (!string.IsNullOrWhiteSpace(map))
        {
            body["map"] = map;
        }
        if (!string.IsNullOrWhiteSpace(scenario))
        {
            body["scenario"] = scenario;
        }
        if (!string.IsNullOrWhiteSpace(cityName))
        {
            body["cityName"] = cityName;
        }
        JsonNode? start = await Post("/commands/new-game", body, cancellationToken).ConfigureAwait(false);
        return await WaitForLoad(start, dryRun ? 0 : waitSeconds, start?["cityName"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How often a load is polled. Tests shorten it.</summary>
    public TimeSpan LoadPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waits for /health to report a level loaded after the one the start reply counted
    /// (loadCount above loadCountBefore), then reads the summary and checks the city name.
    /// </summary>
    private async Task<JsonNode?> WaitForLoad(JsonNode? start, int waitSeconds, string? expectedCity, CancellationToken cancellationToken)
    {
        if (start is not JsonObject startObject || startObject["ok"]?.GetValue<bool>() != true ||
            startObject["loading"]?.GetValue<bool>() != true || waitSeconds <= 0)
        {
            return start;
        }
        int before = startObject["loadCountBefore"]?.GetValue<int>() ?? 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool loaded = false;
        while (watch.Elapsed < TimeSpan.FromSeconds(waitSeconds))
        {
            await Task.Delay(LoadPollInterval, cancellationToken).ConfigureAwait(false);
            JsonNode? health;
            try
            {
                health = await _bridge.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeException)
            {
                continue; // the game can stall the listener for a moment while it loads
            }
            if (health?["levelLoaded"]?.GetValue<bool>() == true && (health["loadCount"]?.GetValue<int>() ?? 0) > before)
            {
                loaded = true;
                break;
            }
        }

        var result = new JsonObject
        {
            ["ok"] = loaded,
            ["start"] = startObject.DeepClone(),
            ["loaded"] = loaded,
            ["waitedSeconds"] = (int)watch.Elapsed.TotalSeconds,
        };
        if (!loaded)
        {
            result["hint"] = "Still loading after " + waitSeconds + " s. Check city_health (loading, loadCount) again before acting.";
            return result;
        }
        JsonNode? summary = await Get("/state/summary", cancellationToken).ConfigureAwait(false);
        result["summary"] = summary?.DeepClone();
        string? actual = summary?["cityName"]?.GetValue<string>();
        bool matches = expectedCity is null || string.Equals(actual, expectedCity, StringComparison.Ordinal);
        result["cityMatches"] = matches;
        if (!matches)
        {
            result["ok"] = false;
            result["hint"] = $"Loaded, but the city is '{actual}', not '{expectedCity}'.";
        }
        return result;
    }

    [System.ComponentModel.Description("GET /prefabs/transport. Transport line prefabs (bus, tram, metro, ...) with their class, economics, unlock milestone and the compatible vehicle assets. Useful for naming the vehicle when managing a line with city_transport_line.")]
    [McpServerTool(Name = "city_transport_prefabs", Title = "Transport Prefabs")]
    public async Task<JsonNode?> CityTransportPrefabs(
        [System.ComponentModel.Description("Optional transport type filter, e.g. 'Bus', 'Metro', 'Train'.")] string? type = null,
        CancellationToken cancellationToken = default)
        => await Get(Query("/prefabs/transport", "type", type), cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ office
    // Everything here is non-spatial: one API call replaces opening the game's
    // menus and moving its sliders. Building lives in BuildTools; districts are
    // painted with the game's own brush through city_district.

    [System.ComponentModel.Description("POST /commands/set-simulation-speed. Set simulation speed (1-3) and/or pause state. This is the office's hands on the clock: pause while you plan a batch of construction, then run at speed 3 so the city reacts.")]
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

    [System.ComponentModel.Description("POST /commands/set-budget. Set a service's day and/or night budget as a percentage (0-300, 100 = baseline). Day is 05:00-20:00; omit nightBudget to leave night alone. Example: 101% on a service buys one extra vehicle per building. The response reports before/after as the game accepted them.")]
    [McpServerTool(Name = "city_set_budget", Title = "Set Budget")]
    public async Task<JsonNode?> CitySetBudget(
        [System.ComponentModel.Description("Service name from city_budgets, e.g. 'Electricity'.")] string service,
        [System.ComponentModel.Description("Day budget percentage (0-300, 100 = baseline).")] int? budget = null,
        [System.ComponentModel.Description("Night budget percentage; omit to leave night unchanged.")] int? nightBudget = null,
        [System.ComponentModel.Description("Optional SubService refinement; default 'None' (the service's own slider).")] string? subService = null,
        [System.ComponentModel.Description("Validate only, do not set (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["service"] = service,
            ["dryRun"] = dryRun,
        };
        if (budget.HasValue)
        {
            body["budget"] = budget.Value;
        }
        if (nightBudget.HasValue)
        {
            body["nightBudget"] = nightBudget.Value;
        }
        if (!string.IsNullOrWhiteSpace(subService))
        {
            body["subService"] = subService;
        }
        return await Post("/commands/set-budget", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/set-policy. Set or unset a policy by exact name from city_policies. Setting an unloaded policy is refused (it would be a silent no-op). district=0 or omitted means city-wide; a district id targets that district. Park-group policies with a district id actually target the park id space.")]
    [McpServerTool(Name = "city_set_policy", Title = "Set Policy")]
    public async Task<JsonNode?> CitySetPolicy(
        [System.ComponentModel.Description("Policy name from city_policies, e.g. 'Recycling', 'FreeTransport', 'TaxRaiseResLow'.")] string policy,
        [System.ComponentModel.Description("True to set, false to unset (default true).")] bool set = true,
        [System.ComponentModel.Description("District id, or 0 for city-wide (default).")] int district = 0,
        [System.ComponentModel.Description("Validate only, do not set (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-policy", new Dictionary<string, object?>
        {
            ["policy"] = policy,
            ["set"] = set,
            ["district"] = district,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/set-building-active. Activate or deactivate a building by id (ids come from city_facilities / city_growables).")]
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

    [System.ComponentModel.Description("POST /commands/empty-building. The info panel's 'Empty building' button: a full cemetery or landfill sends what it stores away (bodies to crematories, garbage to incinerators/other landfills) and takes nothing new while emptying. empty=false stops it. Needs somewhere for the contents to go.")]
    [McpServerTool(Name = "city_empty_building", Title = "Empty Building")]
    public async Task<JsonNode?> CityEmptyBuilding(
        [System.ComponentModel.Description("Building id (a cemetery or landfill, from city_facilities).")] int id,
        [System.ComponentModel.Description("True to start emptying (default), false to stop.")] bool empty = true,
        CancellationToken cancellationToken = default)
        => await Post("/commands/empty-building", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["empty"] = empty,
        }, cancellationToken).ConfigureAwait(false);

    [System.ComponentModel.Description("POST /commands/transport-line. Create and manage transport lines as the line editor does: action='create' (transportInfo, e.g. 'Bus' - needs a depot), action='add-stop' (x,z near a road; the game moves the stop onto the road, read the real positions in lineData.stops; close=true adds the closing stop on the first stop and completes the loop), action='remove-stop' (index, -1 = last), action='activate' (day/night service), action='rename', action='set-vehicle' (asset name from city_transport_prefabs). Every action returns the live game readback for the line.")]
    [McpServerTool(Name = "city_transport_line", Title = "Transport Line Management")]
    public async Task<JsonNode?> CityTransportLine(
        [System.ComponentModel.Description("Action: 'create', 'add-stop', 'remove-stop', 'activate', 'rename' or 'set-vehicle'.")] string action,
        [System.ComponentModel.Description("Line id from city_transport.")] int line = 0,
        [System.ComponentModel.Description("For 'remove-stop': stop index (-1 = last).")] int index = -1,
        [System.ComponentModel.Description("For 'activate': day service (default true).")] bool day = true,
        [System.ComponentModel.Description("For 'activate': night service (default true).")] bool night = true,
        [System.ComponentModel.Description("For 'activate': short for day=true, night=true (default true); false disables all service.")] bool active = true,
        [System.ComponentModel.Description("For 'rename': the new line name.")] string? name = null,
        [System.ComponentModel.Description("For 'set-vehicle': the vehicle asset name.")] string? vehicle = null,
        [System.ComponentModel.Description("For 'create': transport type name from city_transport_prefabs (e.g. 'Bus', 'Metro').")] string? transportInfo = null,
        [System.ComponentModel.Description("For 'add-stop': stop x (world metres, near a road).")] double? x = null,
        [System.ComponentModel.Description("For 'add-stop': stop z.")] double? z = null,
        [System.ComponentModel.Description("For 'add-stop': true adds the closing stop on the first stop (no x/z needed) and completes the loop.")] bool close = false,
        [System.ComponentModel.Description("Validate only, do not mutate (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["line"] = Clamp(line, 0, 65535),
            ["dryRun"] = dryRun,
        };
        if (!string.IsNullOrWhiteSpace(transportInfo))
        {
            body["transportInfo"] = transportInfo;
        }
        if (x.HasValue && z.HasValue)
        {
            body["position"] = new Dictionary<string, object?> { ["x"] = x.Value, ["z"] = z.Value };
        }
        if (close)
        {
            body["close"] = true;
        }
        if (index != -1)
        {
            body["index"] = index;
        }
        if (action == "activate")
        {
            body["day"] = day;
            body["night"] = night;
            body["active"] = active;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            body["name"] = name;
        }
        if (!string.IsNullOrWhiteSpace(vehicle))
        {
            body["vehicle"] = vehicle;
        }
        return await Post("/commands/transport-line", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/district. Districts and special areas, both id spaces (layer 'district' or 'park'). action='create' (name?; for an area also parkType: Generic/AmusementPark/Zoo/NatureReserve, industry Farming/Forestry/Ore/Oil, campus types, ... and parkLevel?) returns the new id, then action='paint' (id + x, z, radius) paints it with the game's own brush (DistrictTool.ApplyBrush: full strength inside radius - 38.4 m, so use radius >= ~100; one cell is 19.2 m). action='erase' (x, z, radius; id? to clear only that one) unpaints. action='rename' (id + name, empty clears), action='specialize' (id + specialization: Forest/Farming/Oil/Ore/Leisure/Tourist/Organic/Selfsufficient/Hightech/ResidentialWallToWall/CommercialWallToWall/OfficeWallToWall/Financial; the game keeps one per family and replaces a competing one; needs the expansion's buildings loaded), action='release' (id: clears its cells and frees it), action='state' (one entity, or the whole list when no id). A special area is not functional until its main gate building is placed. Mutations report the live readback (areaCells before/after).")]
    [McpServerTool(Name = "city_district", Title = "Districts & Areas: Create, Paint, Manage")]
    public async Task<JsonNode?> CityDistrict(
        [System.ComponentModel.Description("Action: 'create', 'paint', 'erase', 'rename', 'specialize', 'release' or 'state'.")] string action,
        [System.ComponentModel.Description("Layer: 'district' (default) or 'park' (a special area).")] string layer = "district",
        [System.ComponentModel.Description("District or area id (0 = none; 'state' with 0 lists everything).")] int id = 0,
        [System.ComponentModel.Description("For 'create' and 'rename': the name (empty clears a custom name).")] string? name = null,
        [System.ComponentModel.Description("For 'specialize': the specialization name from the tool description.")] string? specialization = null,
        [System.ComponentModel.Description("For 'specialize': true to set (default), false to clear.")] bool set = true,
        [System.ComponentModel.Description("For 'create' with layer 'park': the area type (e.g. Generic, AmusementPark, Zoo, NatureReserve, Farming, Forestry, Ore, Oil).")] string? parkType = null,
        [System.ComponentModel.Description("For 'create' with layer 'park': the area level (default Level1).")] string? parkLevel = null,
        [System.ComponentModel.Description("For 'paint' / 'erase': brush centre x (world metres).")] double? x = null,
        [System.ComponentModel.Description("For 'paint' / 'erase': brush centre z (world metres).")] double? z = null,
        [System.ComponentModel.Description("For 'paint' / 'erase': brush radius in metres (default 64; paints nothing below ~77, use >= 100).")] double? radius = null,
        [System.ComponentModel.Description("Validate only, do not mutate (default false).")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["layer"] = layer,
            ["id"] = id,
            ["set"] = set,
            ["dryRun"] = dryRun,
        };
        if (name != null)
        {
            body["name"] = name;
        }
        if (!string.IsNullOrWhiteSpace(specialization))
        {
            body["specialization"] = specialization;
        }
        if (!string.IsNullOrWhiteSpace(parkType))
        {
            body["parkType"] = parkType;
        }
        if (!string.IsNullOrWhiteSpace(parkLevel))
        {
            body["parkLevel"] = parkLevel;
        }
        if (x.HasValue || z.HasValue)
        {
            if (!x.HasValue || !z.HasValue)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "Give both x and z for the brush centre.",
                };
            }
            body["center"] = new Dictionary<string, object?> { ["x"] = x.Value, ["z"] = z.Value };
        }
        if (radius.HasValue)
        {
            body["radius"] = radius.Value;
        }
        return await Post("/commands/district", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/set-camera. The office's eye: frame the game camera on a world position (one from city_problems or an anomaly read), read the current camera state, or toggle edge scrolling so the camera stays put. Use it to show the viewer what was just built (or before a screenshot). x/z given -> the camera snaps to the target the same frame (instant: false lets it glide). distance defaults to keeping the current zoom; verticalAngle/horizontalAngle default to the current framing. edgeScroll true/false flips the live edge-scrolling setting (no restart needed). hideUi true/false hides/shows the whole UI for clean video (readback: camera.uiHidden). Runs on the Unity main thread - the named exception to the simulation-thread rule.")]
    [McpServerTool(Name = "city_camera", Title = "Camera")]
    public async Task<JsonNode?> CityCamera(
        [System.ComponentModel.Description("World x of the target point.")] double? x = null,
        [System.ComponentModel.Description("World z of the target point.")] double? z = null,
        [System.ComponentModel.Description("World height of the target point; omit for sea level.")] double? y = null,
        [System.ComponentModel.Description("Camera distance to the target (default 400).")] double? distance = null,
        [System.ComponentModel.Description("Pitch in degrees: 0 = level with the horizon, 90 = straight down (default: keep current).")] double? verticalAngle = null,
        [System.ComponentModel.Description("Yaw (compass direction the camera faces) in degrees, -180..180 (default: keep current).")] double? horizontalAngle = null,
        [System.ComponentModel.Description("True/false to toggle edge scrolling; omit to leave it alone.")] bool? edgeScroll = null,
        [System.ComponentModel.Description("False = glide to the target over a few frames (smoother on video); default true = snap.")] bool? instant = null,
        [System.ComponentModel.Description("True = hide the game UI, notification icons, district names and the bridge console (the game's free-camera mode) for clean video; false = show them again; omit to leave it.")] bool? hideUi = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (x.HasValue) { body["x"] = x.Value; }
        if (z.HasValue) { body["z"] = z.Value; }
        if (y.HasValue) { body["y"] = y.Value; }
        if (distance.HasValue) { body["distance"] = distance.Value; }
        if (verticalAngle.HasValue) { body["verticalAngle"] = verticalAngle.Value; }
        if (horizontalAngle.HasValue) { body["horizontalAngle"] = horizontalAngle.Value; }
        if (edgeScroll.HasValue) { body["edgeScroll"] = edgeScroll.Value; }
        if (instant.HasValue) { body["instant"] = instant.Value; }
        if (hideUi.HasValue) { body["hideUi"] = hideUi.Value; }
        return await Post("/commands/set-camera", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/project-point. The office's aiming hand: project a world position (x, z; y optional, default 0) to pixels. Returns the Unity screen coordinates, the game window's client rectangle on the desktop, and the absolute cursor pixel (origin top-left) ready for a CUA click - plus onScreen/inFront flags. Re-project after every camera move. Runs on the Unity main thread.")]
    [McpServerTool(Name = "city_project", Title = "Project World Point")]
    public async Task<JsonNode?> CityProject(
        [System.ComponentModel.Description("World x of the point.")] double x,
        [System.ComponentModel.Description("World z of the point.")] double z,
        [System.ComponentModel.Description("World height of the point; omit for sea level.")] double? y = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["x"] = x,
            ["z"] = z,
        };
        if (y.HasValue)
        {
            body["y"] = y.Value;
        }
        return await Post("/commands/project-point", body, cancellationToken).ConfigureAwait(false);
    }

    [System.ComponentModel.Description("POST /commands/save. Save the city to a named slot (.crp on disk). Save after every meaningful batch of work.")]
    [McpServerTool(Name = "city_save", Title = "Save City")]
    public async Task<JsonNode?> CitySave(
        [System.ComponentModel.Description("Save name, e.g. 'MyCity'.")] string name,
        CancellationToken cancellationToken = default)
        => await Post("/commands/save", new Dictionary<string, object?>
        {
            ["name"] = name,
        }, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------- passthrough

    [System.ComponentModel.Description("Raw passthrough for anything with no dedicated tool. GET reaches any endpoint (endpoint list: docs/api.md). POST is limited to endpoints that also have a dedicated tool: /commands/set-simulation-speed, set-tax-rate, set-budget, set-policy, set-building-active, empty-building, unlock-area, transport-line, district, load, new-game, save, set-camera, project-point, build-road, place-building, move-building, zone-segment, set-zone, bulldoze. The legacy repair/batch endpoints are not reachable.")]
    [McpServerTool(Name = "city_request", Title = "Raw Bridge Request")]
    public async Task<JsonNode?> CityRequest(
        [System.ComponentModel.Description("HTTP method: 'GET' or 'POST'.")] string method,
        [System.ComponentModel.Description("Path starting with '/', e.g. '/state/summary'. Query string allowed on GET.")] string path,
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
            string cleanPath = path.Split('?')[0];
            if (!OfficeEndpoints.Contains(cleanPath))
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = $"POST '{cleanPath}' is not on the allowlist",
                    ["allowed"] = new JsonArray(OfficeEndpoints.Select(e => (JsonNode)e).ToArray()),
                    ["hint"] = "Use the dedicated tools (city_build_road, city_place_building, city_zone_segment, ...).",
                };
            }
            JsonNode? body = string.IsNullOrWhiteSpace(jsonBody) ? new JsonObject() : JsonNode.Parse(jsonBody);
            return await Post(path, body, cancellationToken).ConfigureAwait(false);
        }

        return await Get(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The allowlist for <see cref="CityRequest"/> POSTs. Reads stay open for anything;
    /// mutations are limited to endpoints that also have a dedicated tool. The legacy
    /// repair and batch endpoints stay unreachable: they bypass the validated paths.
    /// </summary>
    private static readonly string[] OfficeEndpoints =
    [
        "/commands/set-simulation-speed",
        "/commands/set-tax-rate",
        "/commands/set-budget",
        "/commands/set-policy",
        "/commands/set-building-active",
        "/commands/empty-building",
        "/commands/unlock-area",
        "/commands/transport-line",
        "/commands/district",
        "/commands/load",
        "/commands/new-game",
        "/commands/save",
        "/commands/set-camera",
        "/commands/project-point",
        "/commands/build-road",
        "/commands/place-building",
        "/commands/move-building",
        "/commands/zone-segment",
        "/commands/set-zone",
        "/commands/bulldoze",
    ];

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
