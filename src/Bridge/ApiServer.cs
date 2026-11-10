using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using SkylinesAgentBridge.Http;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// The loopback HTTP listener that exposes the city to the MCP server.
    ///
    /// This class owns the socket and nothing else. The routing table lives in
    /// <see cref="ApiRoutes"/>, and every game-state read or write is funnelled through
    /// the simulation thread by that class.
    ///
    /// THREADING
    /// ---------
    ///   * the accept loop runs on its own background thread ("Skylines Agent Bridge API")
    ///   * each connection is handled on a thread-pool worker
    ///   * neither thread ever touches game state directly
    ///
    /// The server binds to 127.0.0.1 only. It is not reachable from the network.
    /// </summary>
    public sealed class ApiServer
    {
        private readonly AgentBridge bridge;
        private readonly int port;
        private readonly ApiRoutes routes;
        private TcpListener listener;
        private Thread thread;
        private volatile bool running;

        public ApiServer(AgentBridge bridge, int port)
        {
            this.bridge = bridge;
            this.port = port;
            this.routes = new ApiRoutes(bridge, port);
        }

        public bool IsRunning
        {
            get { return running; }
        }

        /// <summary>Starts listening. Safe to call more than once.</summary>
        public void Start()
        {
            if (running)
            {
                return;
            }

            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;

            thread = new Thread(AcceptLoop);
            thread.IsBackground = true;
            thread.Name = "Skylines Agent Bridge API";
            thread.Start();

            Debug.Log("[SkylinesAgentBridge] API server listening on http://127.0.0.1:" + port);
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch (Exception ex)
                {
                    Debug.Log("[SkylinesAgentBridge] API accept failed: " + ex.Message);
                }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;

            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                NetworkStream stream = client.GetStream();
                HttpRequest request = HttpRequest.Read(stream);
                HttpResponse response = routes.Resolve(request);
                response.Write(stream);

                // The in-game console shows what the agent is doing. Notify only enqueues
                // a line; the panel itself is touched on the Unity main thread.
                AgentBridgeNotifier.Notify(routes.Describe(request));
            }
            catch (Exception ex)
            {
                try
                {
                    HttpResponse.Json(500, "{\"ok\":false,\"error\":\"" + JsonUtil.Escape(ex.Message) + "\"}")
                        .Write(client.GetStream());
                }
                catch (Exception)
                {
                    // The socket is already gone. Nothing useful left to do.
                }
            }
            finally
            {
                client.Close();
            }
        }
    }
}
