using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp.Tests;

public sealed class ObsClientTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

    [Fact]
    public void ComputeAuthentication_FollowsTheProtocolFormula()
    {
        const string password = "supersecretpassword";
        const string salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
        const string challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        string expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));

        Assert.Equal(expected, ObsClient.ComputeAuthentication(password, salt, challenge));
    }

    [Fact]
    public async Task Request_AuthenticatesAndReturnsResponseData()
    {
        await using var server = new StubObsServer("secret");
        server.Handler = (type, data) => Task.FromResult(type == "GetVersion"
            ? StubResponse.Success(new JsonObject { ["obsVersion"] = "33.0.1" })
            : new StubResponse(false, 204, "unknown"));
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);

        var data = await client.RequestAsync("GetVersion");

        Assert.Equal("33.0.1", (string?)data?["obsVersion"]);
        Assert.Equal(1, client.ConnectionCount);
        Assert.Equal("5.7.6", client.WebSocketVersion);
        Assert.Equal(ObsClient.SubscribeOutputs, server.LastEventSubscriptions);
    }

    [Fact]
    public async Task Request_PassesRequestData()
    {
        await using var server = new StubObsServer("secret");
        JsonObject? seen = null;
        server.Handler = (_, data) => { seen = data; return Task.FromResult(StubResponse.Success()); };
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);

        var data = await client.RequestAsync("CreateRecordChapter", new JsonObject { ["chapterName"] = "Day 1" });

        Assert.Null(data);
        Assert.Equal("Day 1", (string?)seen?["chapterName"]);
    }

    [Fact]
    public async Task WrongPassword_IsReportedAsAuthenticationFailure()
    {
        await using var server = new StubObsServer("secret");
        await using var client = new ObsClient(server.Url, "wrong", requestTimeout: Short);

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("GetVersion"));

        Assert.Equal(ObsClient.CloseAuthenticationFailed, ex.Code);
        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public async Task NoPasswordConfigured_WhenObsAsksForOne_IsAClearError()
    {
        await using var server = new StubObsServer("secret");
        await using var client = new ObsClient(server.Url, null, requestTimeout: Short);

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("GetVersion"));

        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public async Task ServerWithoutAuthentication_Works()
    {
        await using var server = new StubObsServer(password: null);
        await using var client = new ObsClient(server.Url, null, requestTimeout: Short);

        await client.RequestAsync("GetVersion");

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task FailedRequest_ThrowsWithCodeAndComment()
    {
        await using var server = new StubObsServer("secret");
        server.Handler = (_, _) => Task.FromResult(new StubResponse(false, 501, "Recording is not active."));
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("StopRecord"));

        Assert.Equal(501, ex.Code);
        Assert.Contains("StopRecord", ex.Message);
        Assert.Contains("Recording is not active.", ex.Message);
    }

    [Fact]
    public async Task ConcurrentRequests_AreMatchedById_EvenWhenAnsweredOutOfOrder()
    {
        await using var server = new StubObsServer("secret");
        server.Handler = async (type, _) =>
        {
            if (type == "Slow") await Task.Delay(300);
            return StubResponse.Success(new JsonObject { ["echo"] = type });
        };
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);
        await client.ConnectAsync();

        var slow = client.RequestAsync("Slow");
        var fast = client.RequestAsync("Fast");

        Assert.Equal("Fast", (string?)(await fast)?["echo"]);
        Assert.False(slow.IsCompleted);
        Assert.Equal("Slow", (string?)(await slow)?["echo"]);
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task UnreachableObs_ThrowsWithSetupHint()
    {
        // Grab a free port, then close it so nothing listens there.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using var client = new ObsClient($"ws://127.0.0.1:{port}", "secret", connectTimeout: Short);

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("GetVersion"));

        Assert.Contains("setup-obs.ps1", ex.Message);
    }

    [Fact]
    public async Task DroppedConnection_ReconnectsOnTheNextRequest()
    {
        await using var server = new StubObsServer("secret");
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);
        await client.RequestAsync("GetVersion");

        server.DropAll();
        await WaitUntil(() => !client.IsConnected);
        await client.RequestAsync("GetVersion");

        Assert.Equal(2, client.ConnectionCount);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task DropWhileWaiting_FailsTheRequestInsteadOfRetrying()
    {
        await using var server = new StubObsServer("secret");
        var neverAnswer = new TaskCompletionSource<StubResponse>();
        server.Handler = (_, _) => neverAnswer.Task;
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: TimeSpan.FromSeconds(10));
        await client.ConnectAsync();

        var request = client.RequestAsync("StartRecord");
        await WaitUntil(() => server.Requests.Count == 1);
        server.DropAll();

        var ex = await Assert.ThrowsAsync<ObsException>(() => request);
        Assert.Contains("may or may not have run", ex.Message);
        Assert.Single(server.Requests);   // not resent
    }

    [Fact]
    public async Task UnansweredRequest_TimesOut()
    {
        await using var server = new StubObsServer("secret");
        server.Handler = (_, _) => new TaskCompletionSource<StubResponse>().Task;
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("GetStats"));

        Assert.Contains("did not answer GetStats", ex.Message);
    }

    [Fact]
    public async Task Events_AreRaised()
    {
        await using var server = new StubObsServer("secret");
        await using var client = new ObsClient(server.Url, "secret", requestTimeout: Short);
        var received = new TaskCompletionSource<(string, JsonObject?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (type, data) => received.TrySetResult((type, data));
        await client.ConnectAsync();

        await server.SendEventAsync("RecordStateChanged",
            new JsonObject { ["outputActive"] = true, ["outputState"] = "OBS_WEBSOCKET_OUTPUT_STARTED" });

        var (eventType, eventData) = await received.Task.WaitAsync(Short);
        Assert.Equal("RecordStateChanged", eventType);
        Assert.True((bool?)eventData?["outputActive"]);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }
}

public sealed class StudioSettingsTests
{
    [Fact]
    public void FromFile_ReadsTheSetupScriptFormat()
    {
        string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "obs": {
                "url": "ws://127.0.0.1:4455",
                "password": "pw",
                "scene": "CS1 Mayor",
                "gameCapture": "Game Capture: Cities",
                "recordDirectory": "D:\\cs1-mayor-recordings",
                "executable": "D:\\tools\\obs-studio\\bin\\64bit\\obs64.exe",
                "arguments": "--portable"
              }
            }
            """);
        try
        {
            var settings = StudioSettings.FromFile(path);

            Assert.Equal("ws://127.0.0.1:4455", settings.ObsUrl);
            Assert.Equal("pw", settings.ObsPassword);
            Assert.Equal("CS1 Mayor", settings.Scene);
            Assert.Equal("Game Capture: Cities", settings.GameCapture);
            Assert.Equal(@"D:\cs1-mayor-recordings", settings.RecordDirectory);
            Assert.Equal(Path.GetFullPath(path), settings.SourcePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromFile_FallsBackToDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            var settings = StudioSettings.FromFile(path);

            Assert.Equal("ws://127.0.0.1:4455", settings.ObsUrl);
            Assert.Null(settings.ObsPassword);
            Assert.Equal("CS1 Mayor", settings.Scene);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
