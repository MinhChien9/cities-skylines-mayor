using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Cs1Mayor.Mcp;
using ModelContextProtocol.Protocol;

namespace Cs1Mayor.Mcp.Tests;

public class BuildToolsTests
{
    private static BuildTools Tools(StubHandler handler)
    {
        return new BuildTools(new BridgeClient("http://127.0.0.1:32123", 5, new HttpClient(handler)));
    }

    // ------------------------------------------------------------- request shapes

    [Fact]
    public async Task BuildRoad_posts_points_and_omits_control_for_a_straight_road()
    {
        var handler = StubHandler.Json("{\"ok\":true}");

        await Tools(handler).CityBuildRoad(10, 20, 330, 20, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/commands/build-road", request.Path);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("Basic Road", body["roadPrefab"]!.GetValue<string>());
        Assert.Equal(10, body["start"]!["x"]!.GetValue<double>());
        Assert.Equal(20, body["start"]!["z"]!.GetValue<double>());
        Assert.Equal(330, body["end"]!["x"]!.GetValue<double>());
        Assert.Null(body["control"]);
        Assert.Null(body["name"]);
        Assert.False(body["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public async Task BuildRoad_sends_a_control_point_only_when_both_axes_are_given()
    {
        var handler = StubHandler.Json("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBuildRoad(0, 0, 100, 100, controlX: 100, controlZ: 0, name: "Bend", cancellationToken: CancellationToken.None);
        await tools.CityBuildRoad(0, 0, 100, 100, controlX: 100, cancellationToken: CancellationToken.None);

        var curved = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(100, curved["control"]!["x"]!.GetValue<double>());
        Assert.Equal(0, curved["control"]!["z"]!.GetValue<double>());
        Assert.Equal("Bend", curved["name"]!.GetValue<string>());

        var half = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Null(half["control"]);
    }

    [Fact]
    public async Task PlaceBuilding_posts_prefab_position_and_angle()
    {
        var handler = StubHandler.Json("{\"ok\":true}");

        await Tools(handler).CityPlaceBuilding("Water Tower", 470, -680, 90, dryRun: true, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/place-building", request.Path);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("Water Tower", body["buildingPrefab"]!.GetValue<string>());
        Assert.Equal(470, body["position"]!["x"]!.GetValue<double>());
        Assert.Equal(-680, body["position"]!["z"]!.GetValue<double>());
        Assert.Equal(90, body["angleDegrees"]!.GetValue<double>());
        Assert.True(body["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MoveBuilding_keeps_the_angle_unless_one_is_given()
    {
        var handler = StubHandler.Json("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityMoveBuilding(42, 1, 2, cancellationToken: CancellationToken.None);
        await tools.CityMoveBuilding(42, 1, 2, angleDegrees: 45, cancellationToken: CancellationToken.None);

        Assert.Null(JsonNode.Parse(handler.Requests[0].Body!)!["angleDegrees"]);
        Assert.Equal(45, JsonNode.Parse(handler.Requests[1].Body!)!["angleDegrees"]!.GetValue<double>());
        Assert.All(handler.Requests, r => Assert.Equal("/commands/move-building", r.Path));
    }

    [Fact]
    public async Task ZoneSegment_and_ZoneArea_hit_their_endpoints()
    {
        var handler = StubHandler.Json("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityZoneSegment(1234, "ResidentialLow", side: "left", depth: 2, cancellationToken: CancellationToken.None);
        await tools.CityZoneArea(5, 6, 40, "Industrial", cancellationToken: CancellationToken.None);

        Assert.Equal("/commands/zone-segment", handler.Requests[0].Path);
        var segment = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal(1234, segment["segmentId"]!.GetValue<int>());
        Assert.Equal("left", segment["side"]!.GetValue<string>());
        Assert.Equal(2, segment["depth"]!.GetValue<int>());

        Assert.Equal("/commands/set-zone", handler.Requests[1].Path);
        var area = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal(5, area["center"]!["x"]!.GetValue<double>());
        Assert.Equal(40, area["radius"]!.GetValue<double>());
    }

    [Fact]
    public async Task Bulldoze_posts_the_entity()
    {
        var handler = StubHandler.Json("{\"ok\":true}");

        await Tools(handler).CityBulldoze("netSegment", 77, keepNodes: true, cancellationToken: CancellationToken.None);

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal("netSegment", body["entityType"]!.GetValue<string>());
        Assert.Equal(77, body["id"]!.GetValue<int>());
        Assert.True(body["keepNodes"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Build_tools_turn_a_dead_bridge_into_an_agent_readable_error()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var result = await Tools(handler).CityBuildRoad(0, 0, 1, 1, cancellationToken: CancellationToken.None);

        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Contains("city_health", result["hint"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the map

    /// <summary>A 16×16 map: water in the top-left cell, a road row, one locked cell.</summary>
    private static string MapJson()
    {
        const int n = 16;
        var classes = new byte[n * n];
        classes[0] = 1;                       // water
        for (int c = 0; c < n; c++) { classes[8 * n + c] = 2; } // a road across the middle
        classes[n * n - 1] = 0x80;            // locked land
        var heights = new byte[n * n * 2];
        for (int i = 0; i < n * n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(heights.AsSpan(i * 2), (ushort)(i * 4));
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["bounds"] = new JsonObject { ["west"] = -64, ["east"] = 64, ["south"] = -64, ["north"] = 64 },
            ["cell"] = 8,
            ["cols"] = n,
            ["rows"] = n,
            ["roadDeadEnds"] = new JsonArray(new JsonObject { ["nodeId"] = 5, ["x"] = 60, ["z"] = 0 }),
            ["classes"] = Convert.ToBase64String(classes),
            ["heights"] = Convert.ToBase64String(heights),
        }.ToJsonString();
    }

    [Fact]
    public void MapRenderer_writes_a_valid_png_of_the_expected_size()
    {
        var rendered = MapRenderer.Render(JsonNode.Parse(MapJson())!);

        Assert.Equal(MapRenderer.TargetPixels / 16, rendered.Scale);
        var (width, height, rgb) = DecodePng(rendered.Png);
        Assert.Equal(16 * rendered.Scale, width);
        Assert.Equal(width, height);

        // The road row is dark grey; sample its middle, away from grid lines and labels.
        int y = 8 * rendered.Scale + rendered.Scale / 2;
        int x = 10 * rendered.Scale + rendered.Scale / 2;
        int o = (y * width + x) * 3;
        Assert.Equal((64, 64, 64), (rgb[o], rgb[o + 1], rgb[o + 2]));
    }

    [Fact]
    public async Task CityMap_returns_an_image_and_a_summary_without_the_raw_layers()
    {
        var handler = StubHandler.Json(MapJson());

        CallToolResult result = await Tools(handler).CityMap(0, 0, 128, 8, CancellationToken.None);

        Assert.Equal("/state/map?x=0&z=0&size=128&cell=8", Assert.Single(handler.Requests).Path);
        Assert.NotEqual(true, result.IsError);
        var image = Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        var text = Assert.IsType<TextContentBlock>(result.Content[1]);
        var summary = JsonNode.Parse(text.Text)!;
        Assert.Null(summary["classes"]);
        Assert.Null(summary["heights"]);
        Assert.NotNull(summary["image"]!["pixelToWorld"]);
        Assert.Equal(5, summary["roadDeadEnds"]![0]!["nodeId"]!.GetValue<int>());
    }

    [Fact]
    public async Task CityMap_reports_a_bridge_failure_as_a_tool_error()
    {
        var handler = StubHandler.Json("{\"ok\":false,\"error\":\"No city is loaded.\"}");

        CallToolResult result = await Tools(handler).CityMap(cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
    }

    /// <summary>Just enough PNG decoding for the renderer's own output (RGB8, filter 0).</summary>
    private static (int Width, int Height, byte[] Rgb) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int pos = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(2, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * (width * 3 + 1)]);
            Array.Copy(bytes, y * (width * 3 + 1) + 1, rgb, y * width * 3, width * 3);
        }
        return (width, height, rgb);
    }
}
