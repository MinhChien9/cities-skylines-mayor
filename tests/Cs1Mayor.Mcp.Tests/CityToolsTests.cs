using System.Text.Json.Nodes;
using Cs1Mayor.Mcp;

namespace Cs1Mayor.Mcp.Tests;

public class CityToolsTests
{
    private static CityTools Tools(StubHandler handler)
    {
        return new CityTools(new BridgeClient("http://127.0.0.1:32123", 5, new HttpClient(handler)));
    }

    /// <summary>Answers every request with the same JSON body.</summary>
    private static StubHandler Always(string body)
    {
        return StubHandler.Json(body);
    }

    // ------------------------------------------------------------- 1:1 relays

    [Theory]
    [InlineData("CityHealth", "/health")]
    [InlineData("CityThreads", "/debug/threads")]
    [InlineData("CitySummary", "/state/summary")]
    [InlineData("CityDemand", "/state/demand")]
    [InlineData("CityZones", "/state/zones")]
    [InlineData("CityEconomy", "/state/economy")]
    [InlineData("CitySaves", "/state/saves")]
    [InlineData("CityRoadPrefabs", "/prefabs/roads")]
    [InlineData("CityDisableBlockedAssets", "/commands/disable-blocked-assets")]
    public async Task Tools_relay_their_endpoint_one_to_one(string toolName, string expectedPath)
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        var method = typeof(CityTools).GetMethod(toolName)!;
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        int ctIndex = parameters.Length - 1;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i == ctIndex)
            {
                arguments[i] = CancellationToken.None;
            }
            else
            {
                var type = parameters[i].ParameterType;
                arguments[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }
        }

        var result = (JsonNode?)await (Task<JsonNode?>)method.Invoke(tools, arguments)!;

        Assert.Equal(expectedPath, handler.Requests[0].Path);
        Assert.True(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CitySummary_returns_the_bridge_body_unchanged()
    {
        var tools = Tools(Always("{\"ok\":true,\"population\":123}"));

        var result = await tools.CitySummary(CancellationToken.None);

        Assert.Equal(123, result!["population"]!.GetValue<int>());
        // Raw relay: no nesting, no extra keys added by the server.
        Assert.Null(result["summary"]);
    }

    [Fact]
    public async Task CityProblems_clamps_the_limit_into_the_supported_range()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityProblems(limit: 9999, CancellationToken.None);
        Assert.Contains("limit=500", handler.Requests[0].Path);

        await tools.CityProblems(limit: 0, CancellationToken.None);
        Assert.Contains("limit=1", handler.Requests[1].Path);
    }

    [Fact]
    public async Task CityFacilities_passes_the_service_filter_and_map_objects_flag()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityFacilities(limit: 10, service: "Education", includeMapObjects: true, CancellationToken.None);

        var path = handler.Requests[0].Path;
        Assert.Contains("limit=10", path);
        Assert.Contains("service=Education", path);
        Assert.Contains("includeMapObjects=true", path);
    }

    [Fact]
    public async Task CityFacilities_omits_the_filter_when_none_is_given()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityFacilities(cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("service=", handler.Requests[0].Path);
        Assert.DoesNotContain("includeMapObjects=", handler.Requests[0].Path);
    }

    // ---------------------------------------------------------------- posts

    [Fact]
    public async Task CityBuildRoad_posts_the_segment_and_dry_run_flag()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBuildRoad("Basic Road", 10, 20, 30, 40, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/commands/build-road", request.Path);
        Assert.Contains("\"start\":{\"x\":10,\"z\":20}", request.Body);
        Assert.Contains("\"end\":{\"x\":30,\"z\":40}", request.Body);
        Assert.Contains("\"dryRun\":false", request.Body);
    }

    [Fact]
    public async Task CityZone_posts_the_zone_type_and_radius()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityZone("ResidentialLow", 5, 6, radius: 25, dryRun: true, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/set-zone", request.Path);
        Assert.Contains("\"zone\":\"ResidentialLow\"", request.Body);
        Assert.Contains("\"radius\":25", request.Body);
        Assert.Contains("\"dryRun\":true", request.Body);
    }

    [Fact]
    public async Task CitySetTaxRate_posts_service_and_rate()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CitySetTaxRate("Commercial", 9, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/set-tax-rate", request.Path);
        Assert.Contains("\"service\":\"Commercial\"", request.Body);
        Assert.Contains("\"rate\":9", request.Body);
    }

    [Fact]
    public async Task CityBulldoze_posts_entity_type_and_id()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBulldoze("netSegment", 1234, keepNodes: true, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/bulldoze", request.Path);
        Assert.Contains("\"entityType\":\"netSegment\"", request.Body);
        Assert.Contains("\"id\":1234", request.Body);
        Assert.Contains("\"keepNodes\":true", request.Body);
    }

    [Fact]
    public async Task CitySetSimulationSpeed_posts_speed_and_paused()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CitySetSimulationSpeed(3, false, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/set-simulation-speed", request.Path);
        Assert.Contains("\"speed\":3", request.Body);
        Assert.Contains("\"paused\":false", request.Body);
    }

    [Fact]
    public async Task CitySave_posts_the_name()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CitySave("MyCity", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/save", request.Path);
        Assert.Contains("\"name\":\"MyCity\"", request.Body);
    }

    [Fact]
    public async Task CityBatch_posts_a_parsed_commands_array()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBatch(
            "[{\"type\":\"build-road\",\"roadPrefab\":\"Basic Road\",\"start\":{\"x\":1,\"z\":2},\"end\":{\"x\":3,\"z\":4}}]",
            stopOnError: false,
            dryRun: false,
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/batch", request.Path);
        Assert.Equal("POST", request.Method);
        Assert.Contains("\"type\":\"build-road\"", request.Body);
        Assert.Contains("\"stopOnError\":false", request.Body);
        Assert.Contains("\"dryRun\":false", request.Body);
    }

    // ------------------------------------------------------------ passthrough

    [Fact]
    public async Task CityRequest_relays_get_without_a_body()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        var result = await tools.CityRequest("get", "/state/demand", null, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/state/demand", request.Path);
        Assert.True(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CityRequest_relays_post_with_the_json_body()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityRequest("POST", "/commands/build-network",
            "{\"roadPrefab\":\"Basic Road\",\"start\":{\"x\":0,\"z\":0},\"end\":{\"x\":1,\"z\":1}}",
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/commands/build-network", request.Path);
        Assert.Contains("\"roadPrefab\":\"Basic Road\"", request.Body);
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task CityRequest_rejects_other_methods(string method)
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        var result = await tools.CityRequest(method, "/state/summary", null, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Contains("GET or POST", result["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task CityRequest_rejects_paths_that_do_not_start_with_a_slash()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        var result = await tools.CityRequest("GET", "state/summary", null, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.False(result!["ok"]!.GetValue<bool>());
    }

    // -------------------------------------------------------------- failures

    [Fact]
    public async Task Tools_return_a_structured_error_when_the_bridge_is_down()
    {
        var tools = Tools(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var result = await tools.CitySummary(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Contains("Could not reach", result["error"]!.GetValue<string>());
        Assert.NotNull(result["hint"]);
    }
}
