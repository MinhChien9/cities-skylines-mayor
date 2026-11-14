using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp.Tests;

public sealed class CameraPathTests
{
    private static readonly CameraFrame Start = new(100, 0, -200, 600, 30, 0);

    [Fact]
    public void Orbit_SweepsTheAskedDegrees_TheLongWayIfAsked()
    {
        var path = CameraPath.Orbit(Start, 270);

        Assert.Equal(0, path.At(0).HorizontalAngle, 6);
        Assert.Equal(135, path.At(0.5).HorizontalAngle, 6);
        Assert.Equal(-90, path.At(1).HorizontalAngle, 6);   // 270 wraps to -90
        Assert.Equal(Start.X, path.At(0.5).X);
        Assert.Equal(Start.Distance, path.At(0.5).Distance);
    }

    [Fact]
    public void Orbit_Negative_GoesTheOtherWay()
    {
        Assert.Equal(-45, CameraPath.Orbit(Start, -90).At(0.5).HorizontalAngle, 6);
    }

    [Fact]
    public void At_IsEasedAndClamped()
    {
        var path = CameraPath.Zoom(Start, 200);

        Assert.Equal(600, path.At(-1).Distance);
        Assert.Equal(200, path.At(2).Distance);
        Assert.Equal(400, path.At(0.5).Distance, 6);           // smoothstep(0.5) = 0.5
        Assert.True(path.At(0.1).Distance > 600 - 400 * 0.1);   // slow start
    }

    [Theory]
    [InlineData(0, 0, 0, 100, 0)]      // north (+z)
    [InlineData(0, 0, 100, 0, 90)]     // east (+x)
    [InlineData(0, 0, 0, -100, 180)]   // south
    [InlineData(0, 0, -100, 0, -90)]   // west
    public void Heading_IsACompassBearing(double x, double z, double toX, double toZ, double expected)
    {
        Assert.Equal(expected, CameraPath.Heading(x, z, toX, toZ), 6);
    }

    [Fact]
    public void Flyover_FacesTheTravelDirection_AndMoves()
    {
        var path = CameraPath.Flyover(Start, 500, -200, 400);

        Assert.Equal(90, path.At(0).HorizontalAngle, 6);
        Assert.Equal(90, path.At(1).HorizontalAngle, 6);
        Assert.Equal(300, path.At(0.5).X, 6);
        Assert.Equal(400, path.At(1).Distance, 6);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, 180)]
    [InlineData(-180, 180)]
    public void WrapYaw_StaysInRange(double input, double expected)
    {
        Assert.Equal(expected, CameraPath.WrapYaw(input), 6);
    }
}

public sealed class CinematicToolsTests : IDisposable
{
    private const string CameraState =
        "{\"ok\":true,\"action\":\"state\",\"camera\":{\"target\":{\"x\":330,\"y\":128,\"z\":-640},\"angle\":{\"vertical\":40,\"horizontal\":10},\"distance\":700,\"uiHidden\":false}}";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cs1-viewpoints-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private (CinematicTools Tools, StubHandler Handler) Make()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CameraState, Encoding.UTF8, "application/json"),
        });
        var bridge = new BridgeClient("http://127.0.0.1:32123", 5, new HttpClient(handler));
        return (new CinematicTools(bridge, new ViewpointStore(_file)), handler);
    }

    private static List<JsonNode> Bodies(StubHandler handler)
        => handler.Requests.Select(r => JsonNode.Parse(r.Body!)!).ToList();

    [Fact]
    public async Task Orbit_CutsToTheStart_GlidesAndRestoresTheUi()
    {
        var (tools, handler) = Make();

        var result = await tools.StudioCameraMove("orbit", degrees: 60, seconds: 2);

        Assert.True((bool?)result!["ok"]);
        var bodies = Bodies(handler);
        Assert.All(handler.Requests, r => Assert.Equal("/commands/set-camera", r.Path));
        Assert.Empty(bodies[0].AsObject());                         // state read
        Assert.True((bool?)bodies[1]["instant"]);                   // cut to the start
        Assert.True((bool?)bodies[1]["hideUi"]);
        Assert.Equal(330, (double?)bodies[1]["x"]);                 // current target kept
        Assert.Equal(10, (double?)bodies[1]["horizontalAngle"]);
        var glides = bodies.Skip(2).Take(bodies.Count - 3).ToList();
        Assert.True(glides.Count > 20);
        Assert.All(glides, b => Assert.False((bool?)b["instant"]));
        Assert.Equal(70.0, (double)glides[^1]["horizontalAngle"]!, 3);
        Assert.False((bool?)bodies[^1]["hideUi"]);                  // restored
        Assert.True((bool?)result["uiRestored"]);
    }

    [Fact]
    public async Task Move_KeepsUiShownWhenAsked()
    {
        var (tools, handler) = Make();

        await tools.StudioCameraMove("zoom", toDistance: 300, seconds: 2, hideUi: false);

        Assert.DoesNotContain(Bodies(handler), b => b["hideUi"] is not null);
    }

    [Theory]
    [InlineData("spin", null, null, null)]
    [InlineData("flyover", 10.0, null, null)]
    [InlineData("zoom", null, null, null)]
    public async Task Move_ValidatesBeforeTouchingTheCamera(string kind, double? toX, double? toZ, double? toDistance)
    {
        var (tools, handler) = Make();

        var result = await tools.StudioCameraMove(kind, toX: toX, toZ: toZ, toDistance: toDistance);

        Assert.False((bool?)result!["ok"]);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Viewpoint_SaveGoListDelete()
    {
        var (tools, handler) = Make();

        var saved = await tools.StudioViewpoint("save", "town-overview");
        Assert.True((bool?)saved!["ok"]);
        Assert.Equal(128, (double?)saved["viewpoint"]!["y"]);

        var list = await tools.StudioViewpoint("list");
        Assert.NotNull(list!["viewpoints"]!["town-overview"]);

        handler.Requests.Clear();
        var went = await tools.StudioViewpoint("go", "town-overview", glide: true, hideUi: true);
        Assert.True((bool?)went!["ok"]);
        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        Assert.Equal(700, (double?)body["distance"]);
        Assert.Equal(10, (double?)body["horizontalAngle"]);
        Assert.False((bool?)body["instant"]);
        Assert.True((bool?)body["hideUi"]);

        Assert.True((bool?)(await tools.StudioViewpoint("delete", "town-overview"))!["ok"]);
        Assert.False((bool?)(await tools.StudioViewpoint("go", "town-overview"))!["ok"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("../x")]
    [InlineData("has space")]
    public async Task Viewpoint_RejectsBadNames(string? name)
    {
        var (tools, handler) = Make();

        var result = await tools.StudioViewpoint("save", name);

        Assert.False((bool?)result!["ok"]);
        Assert.Empty(handler.Requests);
    }
}
