using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SkylinesAgentBridge.Http
{
    /// <summary>
    /// A parsed HTTP request.
    ///
    /// The bridge speaks a deliberately small subset of HTTP/1.1: a request line, headers
    /// (only Content-Length is read), and an optional JSON body. There is no chunked
    /// transfer encoding, no keep-alive and no multipart support, because the only client
    /// is the MCP server running on the same machine.
    /// </summary>
    public sealed class HttpRequest
    {
        /// <summary>Upper-case HTTP verb, e.g. GET or POST.</summary>
        public string Method;

        /// <summary>Path without the query string, e.g. "/state/summary".</summary>
        public string Path;

        /// <summary>Raw query string without the leading "?", e.g. "limit=50".</summary>
        public string Query;

        /// <summary>Request body decoded as UTF-8. Empty string when there is no body.</summary>
        public string Body;

        /// <summary>Reads an integer query parameter, falling back to <paramref name="defaultValue"/>.</summary>
        public int GetQueryInt(string name, int defaultValue)
        {
            string raw = GetQueryRaw(name);
            if (raw == null)
            {
                return defaultValue;
            }

            int value;
            return int.TryParse(raw, out value) ? value : defaultValue;
        }

        /// <summary>Reads a float query parameter, falling back to <paramref name="defaultValue"/>.</summary>
        public float GetQueryFloat(string name, float defaultValue)
        {
            string raw = GetQueryRaw(name);
            if (raw == null)
            {
                return defaultValue;
            }

            float value;
            return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : defaultValue;
        }

        /// <summary>Reads a string query parameter, falling back to <paramref name="defaultValue"/>.</summary>
        public string GetQueryString(string name, string defaultValue)
        {
            string raw = GetQueryRaw(name);
            if (raw == null)
            {
                return defaultValue;
            }

            return Uri.UnescapeDataString(raw.Replace("+", " "));
        }

        /// <summary>Reads a boolean query parameter. Only the literal "true" is true.</summary>
        public bool GetQueryBool(string name, bool defaultValue)
        {
            string raw = GetQueryRaw(name);
            if (raw == null)
            {
                return defaultValue;
            }

            return string.Compare(raw, "true", StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>Returns the raw (still percent-encoded) value of a query parameter, or null.</summary>
        private string GetQueryRaw(string name)
        {
            if (Query == null || Query.Length == 0)
            {
                return null;
            }

            string[] pairs = Query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] parts = pairs[i].Split('=');
                if (parts.Length == 2 && parts[0] == name)
                {
                    return parts[1];
                }
            }

            return null;
        }

        /// <summary>
        /// Reads one request off the socket.
        ///
        /// Header size is capped at 64 KB so a malformed or hostile client cannot make the
        /// bridge allocate without bound.
        /// </summary>
        public static HttpRequest Read(NetworkStream stream)
        {
            MemoryStream headerBytes = new MemoryStream();
            int matched = 0;

            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                {
                    break;
                }

                headerBytes.WriteByte((byte)b);

                if ((matched == 0 && b == '\r') ||
                    (matched == 1 && b == '\n') ||
                    (matched == 2 && b == '\r') ||
                    (matched == 3 && b == '\n'))
                {
                    matched++;
                    if (matched == 4)
                    {
                        break;
                    }
                }
                else
                {
                    matched = b == '\r' ? 1 : 0;
                }

                if (headerBytes.Length > 65536)
                {
                    throw new InvalidOperationException("Request headers are too large.");
                }
            }

            string headers = Encoding.UTF8.GetString(headerBytes.ToArray());
            string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            int contentLength = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon > 0 && string.Compare(line.Substring(0, colon), "Content-Length", true) == 0)
                {
                    int.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
                }
            }

            byte[] bodyBytes = new byte[contentLength];
            int offset = 0;
            while (offset < contentLength)
            {
                int read = stream.Read(bodyBytes, offset, contentLength - offset);
                if (read <= 0)
                {
                    break;
                }
                offset += read;
            }

            HttpRequest request = new HttpRequest();
            request.Method = first.Length > 0 ? first[0].ToUpperInvariant() : "";
            string target = first.Length > 1 ? first[1] : "/";
            int queryIndex = target.IndexOf('?');
            if (queryIndex >= 0)
            {
                request.Path = target.Substring(0, queryIndex);
                request.Query = target.Substring(queryIndex + 1);
            }
            else
            {
                request.Path = target;
                request.Query = "";
            }
            request.Body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
            return request;
        }
    }
}
