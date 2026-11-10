using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SkylinesAgentBridge.Http
{
    /// <summary>
    /// An HTTP response carrying a JSON body.
    ///
    /// Every endpoint in the bridge answers with JSON, including the error paths, so the
    /// MCP server can parse one shape and never has to guess whether it got HTML.
    /// </summary>
    public sealed class HttpResponse
    {
        private readonly int status;
        private readonly string body;

        private HttpResponse(int status, string body)
        {
            this.status = status;
            this.body = body;
        }

        /// <summary>Creates a JSON response with the given HTTP status code.</summary>
        public static HttpResponse Json(int status, string body)
        {
            return new HttpResponse(status, body);
        }

        /// <summary>Writes the status line, headers and body to the socket.</summary>
        public void Write(NetworkStream stream)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }

        private static string Reason(int status)
        {
            if (status == 200) return "OK";
            if (status == 404) return "Not Found";
            if (status == 409) return "Conflict";
            return "Internal Server Error";
        }
    }
}
