using System;
using System.Collections.Generic;
using ColossalFramework;
using SkylinesAgentBridge.Commands;
using SkylinesAgentBridge.Http;
using SkylinesAgentBridge.State;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// The bridge's HTTP surface: one table mapping (method, path) to a handler.
    ///
    /// The description next to each route is what the in-game console shows, so a route
    /// only has to be registered once.
    ///
    /// Handlers that touch city state go through <see cref="OnGameThread"/>, which hands
    /// the work to the CS1 simulation thread and blocks the HTTP worker until the
    /// simulation thread has finished it. Handlers that do not touch city state
    /// (/health, /debug/threads) answer immediately.
    /// </summary>
    internal sealed class ApiRoutes
    {
        /// <summary>How long a command may take on the simulation thread before it is abandoned.</summary>
        private const int CommandTimeoutMs = 10000;

        private readonly AgentBridge bridge;
        private readonly int port;
        private readonly List<Route> routes = new List<Route>();

        private sealed class Route
        {
            public string Method;
            public string Path;
            public Func<HttpRequest, string> Describe;
            public Func<HttpRequest, HttpResponse> Handler;
        }

        public ApiRoutes(AgentBridge bridge, int port)
        {
            this.bridge = bridge;
            this.port = port;

            // ---- liveness and diagnostics -------------------------------------
            MapGet("/health", "Liveness probe",
                delegate { return Health(); });

            MapGet("/debug/threads", "Thread diagnostics (the go/no-go proof)",
                delegate { return HttpResponse.Json(200, BuildThreadDiagnostics()); });

            // ---- read endpoints ------------------------------------------------
            MapGet("/state/summary", "Read city summary",
                delegate { return OnGameThread(delegate { return GameState.BuildSummaryJson(); }); });

            MapGet("/state/problems", "Read active problem icons",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 200);
                    return OnGameThread(delegate { return GameState.BuildProblemsJson(limit); });
                });

            MapGet("/state/demand", "Read R/C/I demand",
                delegate { return OnGameThread(delegate { return GameState.BuildDemandJson(); }); });

            MapGet("/state/chirps", "Read citizen chirps",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 50);
                    return OnGameThread(delegate { return GameState.BuildChirpsJson(limit); });
                });

            MapGet("/state/zones", "Read zoning summary",
                delegate { return OnGameThread(delegate { return GameState.BuildZonesJson(); }); });

            MapGet("/state/economy", "Read economy state",
                delegate { return OnGameThread(delegate { return GameState.BuildEconomyJson(); }); });

            MapGet("/state/facilities", "Read service facilities",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 500);
                    string service = request.GetQueryString("service", "");
                    bool includeMapObjects = request.GetQueryBool("includeMapObjects", false);
                    return OnGameThread(delegate { return GameState.BuildFacilitiesJson(limit, service, includeMapObjects); });
                });

            MapGet("/state/growables", "Read growable buildings",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 500);
                    string service = request.GetQueryString("service", "");
                    return OnGameThread(delegate { return GameState.BuildGrowablesJson(limit, service); });
                });

            MapGet("/state/networks", "Read networks",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 500);
                    string service = request.GetQueryString("service", "");
                    return OnGameThread(delegate { return GameState.BuildNetworksJson(limit, service); });
                });

            MapGet("/state/road-anomalies", "Inspect road anomalies",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 200);
                    float nearMissDistance = request.GetQueryFloat("nearMissDistance", 16f);
                    float shortSegmentLength = request.GetQueryFloat("shortSegmentLength", 28f);
                    bool includeDeadEnds = request.GetQueryBool("includeDeadEnds", true);
                    return OnGameThread(delegate { return GameState.BuildRoadAnomaliesJson(limit, nearMissDistance, shortSegmentLength, includeDeadEnds); });
                });

            MapGet("/state/external-connections", "Inspect external connections",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 50);
                    return OnGameThread(delegate { return GameState.BuildExternalConnectionsJson(limit); });
                });

            MapGet("/state/building-anomalies", "Inspect building placement",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 200);
                    return OnGameThread(delegate { return GameState.BuildBuildingAnomaliesJson(limit); });
                });

            MapGet("/state/zone-anomalies", "Inspect zoning anomalies",
                delegate(HttpRequest request)
                {
                    int limit = request.GetQueryInt("limit", 200);
                    int minMinorityCells = request.GetQueryInt("minMinorityCells", 3);
                    int minUnzonedCells = request.GetQueryInt("minUnzonedCells", 6);
                    bool includeUnzonedHoles = request.GetQueryBool("includeUnzonedHoles", true);
                    return OnGameThread(delegate { return GameState.BuildZoneAnomaliesJson(limit, minMinorityCells, minUnzonedCells, includeUnzonedHoles); });
                });

            MapGet("/state/saves", "List save files",
                delegate { return OnGameThread(delegate { return SaveCommands.ListSaves(); }); });

            // ---- prefab catalogue ------------------------------------------------
            MapGet("/prefabs/roads", "List road prefabs",
                delegate { return OnGameThread(delegate { return GameState.BuildRoadPrefabsJson(); }); });

            MapGet("/prefabs/networks", "List network prefabs",
                delegate(HttpRequest request)
                {
                    string service = request.GetQueryString("service", "");
                    return OnGameThread(delegate { return GameState.BuildNetworkPrefabsJson(service); });
                });

            MapGet("/prefabs/buildings", "List building prefabs",
                delegate(HttpRequest request)
                {
                    string service = request.GetQueryString("service", "");
                    return OnGameThread(delegate { return GameState.BuildBuildingPrefabsJson(service); });
                });

            // ---- commands ---------------------------------------------------------
            MapPost("/commands/build-road", "Build a road segment",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return RoadCommands.BuildRoad(body); });
                });

            // "/commands/build-network" is the original name of the same action. It is kept
            // because the MCP server and older agent scripts use it.
            MapPost("/commands/build-network", "Build a road segment (alias of build-road)",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return RoadCommands.BuildRoad(body); });
                });

            MapPost("/commands/set-zone", "Paint a zone",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return ZoneCommands.SetZone(body); });
                });

            MapPost("/commands/repair-zones-to-growables", "Repair zones back to growables",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return ZoneCommands.RepairZonesToGrowables(body); });
                });

            MapPost("/commands/repair-zone-clusters", "Repair zoned clusters",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return ZoneCommands.RepairZoneClusters(body); });
                });

            MapPost("/commands/place-building", "Place a building",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return BuildingCommands.PlaceBuilding(body); });
                });

            MapPost("/commands/move-building", "Move a building",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return BuildingCommands.MoveBuilding(body); });
                });

            MapPost("/commands/set-building-active", "Activate or deactivate a building",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return BuildingCommands.SetBuildingActive(body); });
                });

            MapPost("/commands/disable-blocked-assets", "Disable blocked assets",
                delegate { return OnGameThread(delegate { return AssetCommands.DisableBlockedAssets(); }); });

            MapPost("/commands/set-simulation-speed", "Set simulation speed or pause",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return SimulationCommands.SetSimulationSpeed(body); });
                });

            MapPost("/commands/set-tax-rate", "Set a tax rate",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return EconomyCommands.SetTaxRate(body); });
                });

            MapPost("/commands/bulldoze", "Remove a building, segment or node",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return BulldozeCommands.Bulldoze(body); });
                });

            MapPost("/commands/save", "Save the city",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return SaveCommands.Save(body); });
                });

            MapPost("/commands/batch", "Run a batch of commands",
                delegate(HttpRequest request)
                {
                    string body = request.Body;
                    return OnGameThread(delegate { return BatchCommands.Execute(body); });
                });
        }

        /// <summary>
        /// Finds the handler for a request and runs it.
        /// </summary>
        public HttpResponse Resolve(HttpRequest request)
        {
            if (request.Method == "OPTIONS")
            {
                return HttpResponse.Json(200, "{\"ok\":true}");
            }

            foreach (Route route in routes)
            {
                if (route.Method == request.Method && route.Path == request.Path)
                {
                    return route.Handler(request);
                }
            }

            return HttpResponse.Json(404, "{\"ok\":false,\"error\":\"Not found\",\"path\":\"" +
                JsonUtil.Escape(request.Path) + "\"}");
        }

        /// <summary>
        /// A one-line human description of a request, used by the in-game console.
        /// </summary>
        public string Describe(HttpRequest request)
        {
            foreach (Route route in routes)
            {
                if (route.Method == request.Method && route.Path == request.Path)
                {
                    return route.Describe(request);
                }
            }

            return request.Method + " " + request.Path;
        }

        private void MapGet(string path, string description, Func<HttpRequest, HttpResponse> handler)
        {
            routes.Add(new Route
            {
                Method = "GET",
                Path = path,
                Describe = delegate { return description; },
                Handler = handler
            });
        }

        private void MapPost(string path, string description, Func<HttpRequest, HttpResponse> handler)
        {
            routes.Add(new Route
            {
                Method = "POST",
                Path = path,
                Describe = delegate { return description; },
                Handler = handler
            });
        }

        /// <summary>
        /// Runs an action on the CS1 simulation thread and turns the result into a response.
        ///
        /// This is the ONLY way city state may be read or written. See
        /// <see cref="SkylinesAgentBridge.Threading.SimulationDispatcher"/>.
        /// </summary>
        private HttpResponse OnGameThread(Func<CommandResult> action)        {
            if (!bridge.LevelLoaded)
            {
                return HttpResponse.Json(409, "{\"ok\":false,\"error\":\"No city is loaded.\"}");
            }

            CommandResult result = bridge.Dispatcher.RunSync(action, CommandTimeoutMs);
            return HttpResponse.Json(result.Ok ? 200 : 500, result.Json);
        }

        private HttpResponse Health()
        {
            return HttpResponse.Json(200, "{\"ok\":true,\"mod\":\"CS1 Mayor Bridge\",\"levelLoaded\":" +
                JsonUtil.Bool(bridge.LevelLoaded) + ",\"port\":" + port + "}");
        }

        /// <summary>
        /// Builds a diagnostic payload that PROVES which thread executed a command.
        ///
        /// This is the go/no-go gate for the whole project. It reports:
        ///   * the managed id of the Unity main thread
        ///   * the managed id of the CS1 simulation thread
        ///   * the managed id of the thread that actually ran the probe
        ///   * the game's own simulation frame / render frame counters
        ///
        /// A correct bridge reports probeThreadId == simulationThreadId and
        /// probeThreadId != mainThreadId. If probeThreadId == mainThreadId the bridge is
        /// mutating city state on the render thread, which races with the simulation.
        /// </summary>
        private string BuildThreadDiagnostics()
        {
            Threading.SimulationDispatcher dispatcher = bridge.Dispatcher;
            int probeThreadId = 0;
            string gameTime = "";
            int buildIndex = 0;

            CommandResult probe = dispatcher.RunSync(delegate
            {
                probeThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                try
                {
                    SimulationManager simulation = ColossalFramework.Singleton<SimulationManager>.instance;
                    if (simulation != null)
                    {
                        gameTime = simulation.m_currentGameTime.ToString("s");
                        buildIndex = (int)simulation.m_currentBuildIndex;
                    }
                }
                catch (Exception)
                {
                }
                return CommandResult.FromJson("{\"ok\":true}");
            }, 5000);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("{\"ok\":true");
            sb.Append(",\"probeOk\":").Append(JsonUtil.Bool(probe.Ok));
            sb.Append(",\"probeThreadId\":").Append(probeThreadId);
            sb.Append(",\"simulationThreadId\":").Append(dispatcher.SimulationThreadId);
            sb.Append(",\"mainThreadId\":").Append(dispatcher.MainThreadId);
            sb.Append(",\"httpWorkerThreadId\":").Append(System.Threading.Thread.CurrentThread.ManagedThreadId);
            sb.Append(",\"runsOnSimulationThread\":").Append(JsonUtil.Bool(probeThreadId != 0 && probeThreadId == dispatcher.SimulationThreadId));
            sb.Append(",\"runsOnMainThread\":").Append(JsonUtil.Bool(probeThreadId != 0 && probeThreadId == dispatcher.MainThreadId));
            sb.Append(",\"pendingJobs\":").Append(dispatcher.PendingCount);
            sb.Append(",\"gameTime\":\"").Append(JsonUtil.Escape(gameTime)).Append("\"");
            sb.Append(",\"buildIndex\":").Append(buildIndex);
            sb.Append(",\"levelLoaded\":").Append(JsonUtil.Bool(bridge.LevelLoaded));
            sb.Append(",\"notes\":\"probeThreadId must equal simulationThreadId and must NOT equal mainThreadId\"");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
