using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Cs1Mayor.Mcp;

/// <summary>
/// Turns the bridge's <c>/state/map</c> raster into a PNG the agent can look at.
///
/// The raster is semantic (land, water, road, zone, building, locked tile) and carries
/// exact world coordinates, so one small image replaces a series of screenshots: it does
/// not depend on camera angle, zoom, time of day or UI overlays, and every pixel maps
/// back to a world position with <c>x = west + (px / scale + 0.5) * cell</c>.
///
/// Drawing: land is shaded by height (darker = lower), locked map tiles are dimmed,
/// white grid lines with coordinate labels mark every <see cref="GridStep"/> metres, and
/// road dead ends — the natural places to extend the network from — get a white ring.
/// No image library: the PNG is written by hand (8-bit RGB, one zlib IDAT chunk).
/// </summary>
public static class MapRenderer
{
    public const int TargetPixels = 768;

    private static readonly Dictionary<int, (byte R, byte G, byte B)> Palette = new()
    {
        [1] = (52, 104, 186),   // water
        [2] = (64, 64, 64),     // road
        [3] = (232, 136, 36),   // highway
        [4] = (150, 88, 168),   // other network (rail, power line, path)
        [5] = (214, 48, 48),    // service building
        [6] = (240, 196, 56),   // growable building
        [7] = (92, 188, 92),    // residential zone
        [8] = (72, 152, 232),   // commercial zone
        [9] = (226, 214, 84),   // industrial zone
        [10] = (96, 214, 214),  // office zone
    };

    public sealed record Rendered(byte[] Png, int Scale, double GridStep);

    /// <summary>
    /// Colours one cell for a data overlay (value 0 = no data). Traffic paints road cells
    /// green → yellow → red by density; the others tint the base map by the value relative
    /// to the highest value in view (with a floor, so weak signals stay faint): land value
    /// green, pollution brown, noise purple, and service coverage in the info view's own
    /// colour family (police blue, fire orange, health red, education teal, entertainment
    /// pink, transport yellow, death care grey).
    /// </summary>
    public static (byte R, byte G, byte B) Tint(string? overlay, byte value, int max, (byte R, byte G, byte B) baseColor)
    {
        if (value == 0)
        {
            return baseColor;
        }
        if (overlay == "traffic")
        {
            double t = (value - 1) / 254.0;
            return t < 0.5
                ? Mix((40, 190, 70), (240, 210, 40), t * 2)
                : Mix((240, 210, 40), (220, 40, 30), (t - 0.5) * 2);
        }
        (byte R, byte G, byte B) hue = overlay switch
        {
            "landValue" => (30, 210, 110),
            "pollution" => (120, 70, 20),
            "noise" => (160, 60, 200),
            "police" => (40, 90, 230),
            "fire" => (240, 120, 20),
            "health" => (220, 30, 60),
            "education" or "highSchool" or "university" => (20, 170, 170),
            "entertainment" => (230, 60, 170),
            "transport" => (230, 200, 20),
            "deathCare" => (110, 110, 110),
            _ => (230, 40, 30),
        };
        // Scale to the highest value in view, but never below a floor: a town whose land
        // value is 4 everywhere must not look as vivid as one at 60.
        // Coverage is fixed to the game's own scale (byte 250 = rate 500 = full wellbeing
        // effect), so "barely covered" never looks like "fully covered".
        if (IsCoverage(overlay))
        {
            return Mix(baseColor, hue, 0.85 * Math.Min(1.0, value / 250.0));
        }
        double scaleMax = Math.Max(max, overlay switch { "landValue" => 60, "pollution" => 60, "noise" => 80, _ => 1 });
        return Mix(baseColor, hue, 0.8 * Math.Min(1.0, value / scaleMax));
    }

    private static bool IsCoverage(string? overlay) => overlay is "police" or "fire" or "health" or "education"
        or "highSchool" or "university" or "entertainment" or "transport" or "deathCare";

    private static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double t)
        => ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Rendered Render(JsonNode map)
    {
        int n = map["cols"]!.GetValue<int>();
        double cell = map["cell"]!.GetValue<double>();
        double west = map["bounds"]!["west"]!.GetValue<double>();
        double north = map["bounds"]!["north"]!.GetValue<double>();
        byte[] classes = Convert.FromBase64String(map["classes"]!.GetValue<string>());
        byte[] heightBytes = Convert.FromBase64String(map["heights"]!.GetValue<string>());
        byte[]? overlay = map["overlayValues"] is JsonValue ov ? Convert.FromBase64String(ov.GetValue<string>()) : null;
        string? overlayName = map["overlay"]?["name"]?.GetValue<string>();
        int overlayMax = overlay is null ? 1 : Math.Max(1, overlay.Max(b => (int)b));

        var heights = new double[n * n];
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < n * n; i++)
        {
            heights[i] = BinaryPrimitives.ReadUInt16LittleEndian(heightBytes.AsSpan(i * 2, 2)) / 4.0;
            lo = Math.Min(lo, heights[i]);
            hi = Math.Max(hi, heights[i]);
        }
        double span = Math.Max(hi - lo, 1.0);

        int scale = Math.Max(1, TargetPixels / n);
        int size = n * scale;
        var rgb = new byte[size * size * 3];

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int i = r * n + c;
                int cls = classes[i] & 0x7F;
                (byte R, byte G, byte B) color;
                if (Palette.TryGetValue(cls, out var p))
                {
                    color = p;
                }
                else
                {
                    double t = (heights[i] - lo) / span;
                    color = ((byte)(120 + 100 * t), (byte)(116 + 84 * t), (byte)(80 + 64 * t));
                }
                if (overlay is not null)
                {
                    color = Tint(overlayName, overlay[i], overlayMax, color);
                }
                if ((classes[i] & 0x80) != 0)
                {
                    color = ((byte)(color.R * 0.4), (byte)(color.G * 0.4), (byte)(color.B * 0.4));
                }
                for (int dy = 0; dy < scale; dy++)
                {
                    for (int dx = 0; dx < scale; dx++)
                    {
                        Set(rgb, size, c * scale + dx, r * scale + dy, color);
                    }
                }
            }
        }

        // Grid every 128 m (256 m for big maps), labelled with world coordinates.
        double extent = n * cell;
        double step = extent > 1600 ? 256 : 128;
        var white = ((byte)255, (byte)255, (byte)255);
        var black = ((byte)0, (byte)0, (byte)0);
        for (double gx = Math.Ceiling(west / step) * step; gx < west + extent; gx += step)
        {
            int px = (int)Math.Round((gx - west) / cell * scale);
            for (int y = 0; y < size; y += 2) { Set(rgb, size, px, y, white); }
            DrawLabel(rgb, size, px + 3, 3, ((int)gx).ToString(), white, black);
        }
        for (double gz = Math.Floor(north / step) * step; gz > north - extent; gz -= step)
        {
            int py = (int)Math.Round((north - gz) / cell * scale);
            for (int x = 0; x < size; x += 2) { Set(rgb, size, x, py, white); }
            DrawLabel(rgb, size, 3, py + 3, ((int)gz).ToString(), white, black);
        }

        // Dead-end rings.
        if (map["roadDeadEnds"] is JsonArray ends)
        {
            foreach (var e in ends)
            {
                double x = e!["x"]!.GetValue<double>();
                double z = e["z"]!.GetValue<double>();
                int px = (int)Math.Round((x - west) / cell * scale);
                int py = (int)Math.Round((north - z) / cell * scale);
                DrawRing(rgb, size, px, py, Math.Max(4, scale * 2), white);
            }
        }

        return new Rendered(EncodePng(rgb, size, size), scale, step);
    }

    private static void Set(byte[] rgb, int size, int x, int y, (byte R, byte G, byte B) c)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) { return; }
        int o = (y * size + x) * 3;
        rgb[o] = c.R;
        rgb[o + 1] = c.G;
        rgb[o + 2] = c.B;
    }

    private static void DrawRing(byte[] rgb, int size, int cx, int cy, int radius, (byte, byte, byte) c)
    {
        for (int a = 0; a < 64; a++)
        {
            double t = a * Math.PI * 2 / 64;
            Set(rgb, size, cx + (int)Math.Round(Math.Cos(t) * radius), cy + (int)Math.Round(Math.Sin(t) * radius), c);
        }
    }

    // A 3x5 pixel font for the digits and '-', drawn at 2x with a 1 px outline.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["111", "101", "101", "101", "111"],
        ['1'] = ["010", "110", "010", "010", "111"],
        ['2'] = ["111", "001", "111", "100", "111"],
        ['3'] = ["111", "001", "111", "001", "111"],
        ['4'] = ["101", "101", "111", "001", "001"],
        ['5'] = ["111", "100", "111", "001", "111"],
        ['6'] = ["111", "100", "111", "101", "111"],
        ['7'] = ["111", "001", "010", "010", "010"],
        ['8'] = ["111", "101", "111", "101", "111"],
        ['9'] = ["111", "101", "111", "001", "111"],
        ['-'] = ["000", "000", "111", "000", "000"],
    };

    private static void DrawLabel(byte[] rgb, int size, int x, int y, string text, (byte, byte, byte) fg, (byte, byte, byte) bg)
    {
        const int s = 2;
        int width = text.Length * 4 * s;
        for (int yy = -1; yy <= 5 * s; yy++)
        {
            for (int xx = -1; xx <= width; xx++)
            {
                Set(rgb, size, x + xx, y + yy, bg);
            }
        }
        for (int k = 0; k < text.Length; k++)
        {
            if (!Glyphs.TryGetValue(text[k], out var g)) { continue; }
            for (int gy = 0; gy < 5; gy++)
            {
                for (int gx = 0; gx < 3; gx++)
                {
                    if (g[gy][gx] != '1') { continue; }
                    for (int dy = 0; dy < s; dy++)
                    {
                        for (int dx = 0; dx < s; dx++)
                        {
                            Set(rgb, size, x + k * 4 * s + gx * s + dx, y + gy * s + dy, fg);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Minimal PNG writer: 8-bit truecolour, no interlace, filter 0 on every row.</summary>
    public static byte[] EncodePng(byte[] rgb, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) { c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); }
        foreach (byte b in data) { c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); }
        return c ^ 0xFFFFFFFFu;
    }
}
