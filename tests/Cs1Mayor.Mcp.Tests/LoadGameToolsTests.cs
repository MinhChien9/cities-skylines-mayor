using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cs1Mayor.Mcp;

namespace Cs1Mayor.Mcp.Tests;

public class LoadGameToolsTests
{
    private static CityTools Tools(StubHandler handler)
    {
        var tools = new CityTools(new BridgeClient("http://127.0.0.1:32123", 5, new HttpClient(handler)));
        tools.LoadPollInterval = TimeSpan.FromMilliseconds(10);
        return tools;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// A bridge that starts a load, reports it in progress for a few polls, then loaded.
    /// </summary>
    private static StubHandler LoadingBridge(string startReply, string summaryCity, int pollsUntilLoaded = 2)
    {
        int polls = 0;
        return new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/health")
            {
                polls++;
                return polls < pollsUntilLoaded
                    ? Json("{\"ok\":true,\"levelLoaded\":false,\"loading\":true,\"loadCount\":1}")
                    : Json("{\"ok\":true,\"levelLoaded\":true,\"loading\":false,\"loadCount\":2}");
            }
            if (path == "/state/summary")
            {
                return Json("{\"ok\":true,\"cityName\":\"" + summaryCity + "\",\"population\":6665}");
            }
            return Json(startReply);
        });
    }

    [Fact]
    public async Task CityLoadGame_waits_for_the_next_load_and_checks_the_city()
    {
        var handler = LoadingBridge(
            "{\"ok\":true,\"loading\":true,\"cityName\":\"Peach Trees\",\"loadCountBefore\":1}", "Peach Trees");
        var tools = Tools(handler);

        var result = await tools.CityLoadGame("before-load-test", waitSeconds: 5, cancellationToken: CancellationToken.None);

        Assert.Equal("/commands/load", handler.Requests[0].Path);
        Assert.Contains("\"name\":\"before-load-test\"", handler.Requests[0].Body);
        Assert.True(result!["loaded"]!.GetValue<bool>());
        Assert.True(result["cityMatches"]!.GetValue<bool>());
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(6665, result["summary"]!["population"]!.GetValue<int>());
    }

    [Fact]
    public async Task CityLoadGame_reports_a_different_city_as_not_ok()
    {
        var handler = LoadingBridge(
            "{\"ok\":true,\"loading\":true,\"cityName\":\"Peach Trees\",\"loadCountBefore\":1}", "Somewhere Else");
        var tools = Tools(handler);

        var result = await tools.CityLoadGame("before-load-test", waitSeconds: 5, cancellationToken: CancellationToken.None);

        Assert.True(result!["loaded"]!.GetValue<bool>());
        Assert.False(result["cityMatches"]!.GetValue<bool>());
        Assert.False(result["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CityLoadGame_dry_run_does_not_wait()
    {
        var handler = StubHandler.Json("{\"ok\":true,\"dryRun\":true,\"cityName\":\"Peach Trees\",\"loadCountBefore\":1}");
        var tools = Tools(handler);

        var result = await tools.CityLoadGame("before-load-test", dryRun: true, cancellationToken: CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"dryRun\":true", request.Body);
        Assert.True(result!["dryRun"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CityNewGame_posts_the_scenario_and_waits()
    {
        var handler = LoadingBridge(
            "{\"ok\":true,\"loading\":true,\"scenario\":\"City of Gardens\",\"cityName\":\"Bridge Test\",\"loadCountBefore\":0}",
            "Bridge Test");
        var tools = Tools(handler);

        var result = await tools.CityNewGame(scenario: "City of Gardens", cityName: "Bridge Test", leftHandTraffic: true,
            waitSeconds: 5, cancellationToken: CancellationToken.None);

        var start = handler.Requests[0];
        Assert.Equal("/commands/new-game", start.Path);
        Assert.Contains("\"scenario\":\"City of Gardens\"", start.Body);
        Assert.Contains("\"cityName\":\"Bridge Test\"", start.Body);
        Assert.Contains("\"leftHandTraffic\":true", start.Body);
        Assert.DoesNotContain("\"map\"", start.Body);
        Assert.True(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CityNewGame_gives_up_after_waitSeconds()
    {
        var handler = LoadingBridge(
            "{\"ok\":true,\"loading\":true,\"map\":\"Cannes\",\"cityName\":\"Cannes\",\"loadCountBefore\":0}",
            "Cannes", pollsUntilLoaded: int.MaxValue);
        var tools = Tools(handler);

        var result = await tools.CityNewGame(map: "Cannes", waitSeconds: 1, cancellationToken: CancellationToken.None);

        Assert.False(result!["loaded"]!.GetValue<bool>());
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.NotNull(result["hint"]);
    }

    [Fact]
    public async Task CityMaps_relays_the_catalogue()
    {
        var handler = StubHandler.Json("{\"ok\":true,\"maps\":[],\"scenarios\":[]}");
        var tools = Tools(handler);

        await tools.CityMaps(CancellationToken.None);

        Assert.Equal("/state/maps", Assert.Single(handler.Requests).Path);
    }
}
