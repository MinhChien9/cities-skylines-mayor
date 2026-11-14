using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cs1Mayor.Mcp;

/// <summary>
/// The agent's hands: seeing the land and building on it, without a cursor.
///
/// WHY THIS EXISTS (and why it replaced the cursor-only model)
/// -----------------------------------------------------------
/// The first build tools called the game's storage layer directly
/// (<c>NetManager.CreateSegment</c>, <c>BuildingManager.CreateBuilding</c>) and skipped
/// every rule the player's tools enforce, so they produced disconnected, overlapping
/// roads and buildings in rivers. The response was to move all spatial work to a
/// computer-use cursor — which works, but costs a screenshot per step and still has to
/// guess world positions from pixels.
///
/// The bridge now builds through the game's own tool code: <c>NetTool.CreateNode</c> for
/// every network and a port of <c>BuildingTool</c>'s placement rules for buildings,
/// always running the tool's <c>test</c> pass first. The agent gets exactly the snapping,
/// junction-splitting, collision, slope, locked-tile and money checks the player gets,
/// and a refusal comes back as the game's own error names plus a hint.
///
/// <c>city_map</c> is the eye that goes with it: a semantic raster rendered from game
/// data with world coordinates on it, so plans are made in metres, not pixels.
/// </summary>
[McpServerToolType]
public sealed class BuildTools
{
    private readonly BridgeClient _bridge;

    public BuildTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    // ------------------------------------------------------------------- seeing

    [Description("GET /state/map rendered as a PNG: a top-down semantic map of a square area built from game data, not a screenshot. Colours: shaded tan = buildable land (darker = lower), blue = water, dark grey = road, orange = highway, purple = rail/power line/path, red = service building, yellow = growable building, green/blue/yellow/cyan tints = residential/commercial/industrial/office zones, dimmed = locked map tile (cannot build). White dashed lines with labels = world coordinates; white rings = road dead ends (good places to extend from). Pixel -> world: x = west + (px/scale + 0.5)*cell, z = north - (py/scale + 0.5)*cell (north is up, +z). The text part lists the bounds, scale, counts and the dead-end node ids. Use it before planning any construction.")]
    [McpServerTool(Name = "city_map", Title = "Semantic Map", ReadOnly = true)]
    public async Task<CallToolResult> CityMap(
        [Description("World x of the centre.")] double x = 0,
        [Description("World z of the centre.")] double z = 0,
        [Description("Side length in metres (default 1024, max 4096). The start tile is 1920 m wide, centred on 0,0.")] double size = 1024,
        [Description("Metres per cell (default 8 = one zone cell; use 16 for 2048 m overviews).")] double cell = 8,
        CancellationToken cancellationToken = default)
    {
        string path = FormattableString.Invariant($"/state/map?x={x}&z={z}&size={size}&cell={cell}");
        JsonNode? map;
        try
        {
            map = await _bridge.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return ErrorResult(ex.Message);
        }
        if (map is null || map["ok"]?.GetValue<bool>() != true || map["classes"] is null)
        {
            return ErrorResult(map?.ToJsonString() ?? "empty response from /state/map");
        }

        MapRenderer.Rendered image = MapRenderer.Render(map);

        var summary = map.DeepClone().AsObject();
        summary.Remove("classes");
        summary.Remove("heights");
        summary["image"] = new JsonObject
        {
            ["pixelsPerCell"] = image.Scale,
            ["gridStepMeters"] = image.GridStep,
            ["pixelToWorld"] = "x = west + (px / pixelsPerCell + 0.5) * cell; z = north - (py / pixelsPerCell + 0.5) * cell",
        };

        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(image.Png, "image/png"),
                new TextContentBlock { Text = summary.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) },
            ],
        };
    }

    // ------------------------------------------------------------------ networks

    [Description("POST /commands/build-road. Build any network - road, highway, Water Pipe, Power Line, path, rail - through the game's own NetTool.CreateNode, exactly as if the player drew it. Ends snap onto an existing node or segment within snapRadius (default 8 m): snapping onto a segment makes a T-junction. A straight road that runs over existing nodes or crosses existing roads is split there automatically, so crossings become real intersections. Every leg is tested first; nothing is built unless all legs pass. Errors are the game's own (ObjectCollision, SlopeTooSteep, OutOfArea, NotEnoughMoney, TooShort, InvalidShape, ...) plus 'Underwater' (ground road on the sea bed: route over land, or set elevation >= 4 for a bridge). Returns the new segment ids (feed them to city_zone_segment) and the cost. Tip: dryRun first when unsure. Prefab names: 'Basic Road' (2-lane), 'Medium Road' (4-lane), 'Large Road', 'Oneway Road', 'Gravel Road', 'Highway', 'Water Pipe', 'Power Line'; full list via city_request GET /prefabs/networks.")]
    [McpServerTool(Name = "city_build_road", Title = "Build Road / Network")]
    public async Task<JsonNode?> CityBuildRoad(
        [Description("Start x (world metres).")] double startX,
        [Description("Start z.")] double startZ,
        [Description("End x.")] double endX,
        [Description("End z.")] double endZ,
        [Description("Network prefab name (default 'Basic Road').")] string prefab = "Basic Road",
        [Description("Curve control point x. Give controlX and controlZ for a curved road; omit both for straight.")] double? controlX = null,
        [Description("Curve control point z.")] double? controlZ = null,
        [Description("Height above ground in metres (0 = ground; >= 4 builds the elevated/bridge variant).")] double elevation = 0,
        [Description("Street name for every new segment (optional).")] string? name = null,
        [Description("Snap radius in metres for the two ends (default 8, 0 = no snapping).")] double snapRadius = 8,
        [Description("Validate only (test pass), build nothing.")] bool dryRun = false,
        [Description("Allow a ground-level road under water (rarely wanted).")] bool allowWater = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["roadPrefab"] = prefab,
            ["start"] = Point(startX, startZ),
            ["end"] = Point(endX, endZ),
            ["elevation"] = elevation,
            ["snapRadius"] = snapRadius,
            ["dryRun"] = dryRun,
            ["allowWater"] = allowWater,
        };
        if (controlX.HasValue && controlZ.HasValue)
        {
            body["control"] = Point(controlX.Value, controlZ.Value);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            body["name"] = name;
        }
        return await Post("/commands/build-road", body, cancellationToken).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- buildings

    [Description("POST /commands/place-building. Place a building with the building tool's own rules: Roadside buildings (most services: schools, clinics, police, fire) snap to the nearest road frontage within ~8-40 m and turn to face it - aim at the kerb, not the road centre; Shoreline buildings (water intake, outlet) snap to the water's edge within 50 m; free-standing ones (wind turbine, water tower) go exactly where asked at angleDegrees. Collision, slope, unlocked area and money are checked first; refusals carry the game's error names and a hint. The response reports where the building actually went (position, angle, snappedTo). Building names: city_request GET /prefabs/buildings?service=Education (etc.).")]
    [McpServerTool(Name = "city_place_building", Title = "Place Building")]
    public async Task<JsonNode?> CityPlaceBuilding(
        [Description("Building prefab name, e.g. 'Wind Turbine', 'Water Tower', 'Elementary_School_EU', 'medicalclinicEU'.")] string prefab,
        [Description("Target x (world metres).")] double x,
        [Description("Target z.")] double z,
        [Description("Rotation in degrees for free-standing buildings (snapped ones face their road).")] double angleDegrees = 0,
        [Description("Validate only, build nothing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/place-building", new Dictionary<string, object?>
        {
            ["buildingPrefab"] = prefab,
            ["position"] = Point(x, z),
            ["angleDegrees"] = angleDegrees,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [Description("POST /commands/move-building. Relocate an existing building (id from city_facilities) with the same placement rules as city_place_building; the building itself is ignored in the collision check. Moving is free.")]
    [McpServerTool(Name = "city_move_building", Title = "Move Building")]
    public async Task<JsonNode?> CityMoveBuilding(
        [Description("Building id.")] int id,
        [Description("Target x.")] double x,
        [Description("Target z.")] double z,
        [Description("Rotation in degrees (default: keep the current angle).")] double? angleDegrees = null,
        [Description("Validate only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["position"] = Point(x, z),
            ["dryRun"] = dryRun,
        };
        if (angleDegrees.HasValue)
        {
            body["angleDegrees"] = angleDegrees.Value;
        }
        return await Post("/commands/move-building", body, cancellationToken).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------- zones

    [Description("POST /commands/zone-segment. Zone the cells along ONE road segment (ids come back from city_build_road, or from city_networks): side 'left' | 'right' | 'both', depth 1-4 cells back from the road (4 = full 32 m block). Already-zoned cells are kept unless overwrite is true; zone 'Unzoned' clears. Highways, ramps and pipes have no zone cells. Zones: ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, Industrial, Office, Unzoned.")]
    [McpServerTool(Name = "city_zone_segment", Title = "Zone Along Road")]
    public async Task<JsonNode?> CityZoneSegment(
        [Description("Road segment id.")] int segmentId,
        [Description("Zone name.")] string zone,
        [Description("'left', 'right' or 'both' (default).")] string side = "both",
        [Description("Cells back from the road, 1-4 (default 4).")] int depth = 4,
        [Description("Replace cells that already have another zone.")] bool overwrite = false,
        [Description("Count only, change nothing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/zone-segment", new Dictionary<string, object?>
        {
            ["segmentId"] = segmentId,
            ["zone"] = zone,
            ["side"] = side,
            ["depth"] = depth,
            ["overwrite"] = overwrite,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    [Description("POST /commands/set-zone. The zone brush: paint every zone cell whose centre lies within radius of (x, z). Same zone names and overwrite rule as city_zone_segment. Prefer city_zone_segment for streets; use this for filling a district.")]
    [McpServerTool(Name = "city_zone_area", Title = "Zone Area (Brush)")]
    public async Task<JsonNode?> CityZoneArea(
        [Description("Centre x.")] double x,
        [Description("Centre z.")] double z,
        [Description("Brush radius in metres (1-256).")] double radius,
        [Description("Zone name.")] string zone,
        [Description("Replace cells that already have another zone.")] bool overwrite = false,
        [Description("Count only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/set-zone", new Dictionary<string, object?>
        {
            ["center"] = Point(x, z),
            ["radius"] = radius,
            ["zone"] = zone,
            ["overwrite"] = overwrite,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    // ---------------------------------------------------------------- demolition

    [Description("POST /commands/bulldoze. Remove one building, network segment or node by id (ids from city_facilities, city_networks, the anomaly reads or a build response).")]
    [McpServerTool(Name = "city_bulldoze", Title = "Bulldoze", Destructive = true)]
    public async Task<JsonNode?> CityBulldoze(
        [Description("'building', 'netSegment' or 'netNode'.")] string entityType,
        [Description("Entity id.")] int id,
        [Description("For netSegment: keep its end nodes.")] bool keepNodes = false,
        [Description("Check only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await Post("/commands/bulldoze", new Dictionary<string, object?>
        {
            ["entityType"] = entityType,
            ["id"] = id,
            ["keepNodes"] = keepNodes,
            ["dryRun"] = dryRun,
        }, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ internals

    private static Dictionary<string, object?> Point(double x, double z) => new()
    {
        ["x"] = x,
        ["z"] = z,
    };

    private async Task<JsonNode?> Post(string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await _bridge.PostAsync(path, body, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message,
                ["hint"] = "Start Cities: Skylines with the CS1 Mayor Bridge mod enabled and load a city, then retry. See city_health.",
            };
        }
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
