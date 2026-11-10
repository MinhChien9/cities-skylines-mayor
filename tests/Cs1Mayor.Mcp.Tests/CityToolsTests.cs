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

    [Fact]
    public async Task CitySnapshot_nests_summary_demand_and_economy_under_their_own_keys()
    {
        var tools = Tools(Always("{\"ok\":true,\"population\":100}"));

        var result = await tools.CitySnapshot(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!["summary"]);
        Assert.NotNull(result["demand"]);
        Assert.NotNull(result["economy"]);
    }

    [Fact]
    public async Task CitySnapshot_returns_a_structured_error_when_the_bridge_is_down()
    {
        var tools = Tools(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var result = await tools.CitySnapshot(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Contains("Could not reach", result["error"]!.GetValue<string>());
        Assert.NotNull(result["hint"]);
    }

    [Fact]
    public async Task CityProblems_clamps_the_limit_into_the_supported_range()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityProblems(limit: 9999, CancellationToken.None);

        Assert.Contains("/state/problems?limit=200", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CityProblems_clamps_a_zero_limit_up_to_one()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityProblems(limit: 0, CancellationToken.None);

        Assert.Contains("/state/problems?limit=1", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CityServices_appends_the_service_filter_when_one_is_given()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityServices("Education", CancellationToken.None);

        Assert.Contains("service=Education", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CityServices_omits_the_filter_when_none_is_given()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityServices(null, CancellationToken.None);

        Assert.DoesNotContain("service=", handler.Requests[0].Path);
    }

    [Theory]
    [InlineData("roads", "/prefabs/roads")]
    [InlineData("networks", "/prefabs/networks")]
    [InlineData("buildings", "/prefabs/buildings")]
    [InlineData("something-else", "/prefabs/buildings")]
    public async Task CityPrefabs_maps_the_kind_to_the_right_endpoint(string kind, string expected)
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityPrefabs(kind, CancellationToken.None);

        Assert.Equal(expected, handler.Requests[0].Path);
    }

    [Fact]
    public async Task CityBuildRoad_posts_to_the_build_road_endpoint()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBuildRoad("Basic Road", 10, 20, 30, 40, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/commands/build-road", request.Path);
        Assert.Contains("\"start\":{\"x\":10,\"z\":20}", request.Body);
        Assert.Contains("\"end\":{\"x\":30,\"z\":40}", request.Body);
    }

    [Fact]
    public async Task CityBuildRoad_passes_the_dry_run_flag_through()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityBuildRoad("Basic Road", 10, 20, 30, 40, dryRun: true, cancellationToken: CancellationToken.None);

        Assert.Contains("\"dryRun\":true", handler.Requests[0].Body);
    }

    [Fact]
    public async Task CityZone_posts_the_zone_type_and_radius()
    {
        var handler = Always("{\"ok\":true}");
        var tools = Tools(handler);

        await tools.CityZone("ResidentialLow", 5, 6, radius: 25, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/commands/set-zone", request.Path);
        Assert.Contains("\"zone\":\"ResidentialLow\"", request.Body);
        Assert.Contains("\"radius\":25", request.Body);
    }

    [Fact]
    public async Task CityHealth_merges_health_and_thread_diagnostics()
    {
        var tools = Tools(Always("{\"ok\":true,\"runsOnSimulationThread\":true}"));

        var result = await tools.CityHealth(CancellationToken.None);

        Assert.NotNull(result!["health"]);
        Assert.NotNull(result["threads"]);
    }

    [Fact]
    public async Task CitySave_returns_both_the_save_result_and_the_save_list()
    {
        var tools = Tools(Always("{\"ok\":true}"));

        var result = await tools.CitySave("AgentAutoSave", CancellationToken.None);

        Assert.NotNull(result!["save"]);
        Assert.NotNull(result["saves"]);
    }
}
