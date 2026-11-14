namespace Cs1Mayor.Mcp;

/// <summary>One camera framing: the point looked at, the distance and the two angles.</summary>
public readonly record struct CameraFrame(double X, double Y, double Z, double Distance, double VerticalAngle, double HorizontalAngle)
{
    public Dictionary<string, object?> ToBody(bool instant) => new()
    {
        ["x"] = Math.Round(X, 2),
        ["y"] = Math.Round(Y, 2),
        ["z"] = Math.Round(Z, 2),
        ["distance"] = Math.Round(Distance, 2),
        ["verticalAngle"] = Math.Round(VerticalAngle, 3),
        ["horizontalAngle"] = Math.Round(HorizontalAngle, 3),
        ["instant"] = instant,
    };
}

/// <summary>
/// A camera move from one framing to another, sampled by time. Pure math so it can be
/// tested without the game: <see cref="CinematicTools"/> streams the samples to the
/// bridge as glide targets, and the game's own camera smoothing does the rest.
///
/// Positions, distance and pitch interpolate linearly; yaw interpolates by the signed
/// number of degrees asked for (an orbit of 360 goes all the way round, it does not
/// take the short way). Time is eased with smoothstep so moves start and end softly.
/// </summary>
public sealed class CameraPath
{
    private readonly CameraFrame _from;
    private readonly CameraFrame _to;
    private readonly double _yawDegrees;

    public CameraPath(CameraFrame from, CameraFrame to, double? yawDegrees = null)
    {
        _from = from;
        _to = to;
        _yawDegrees = yawDegrees ?? ShortestTurn(from.HorizontalAngle, to.HorizontalAngle);
    }

    public CameraFrame Start => _from;

    /// <summary>The framing at progress <paramref name="t"/> (0..1, clamped), eased.</summary>
    public CameraFrame At(double t)
    {
        double e = Ease(Math.Clamp(t, 0, 1));
        return new CameraFrame(
            Lerp(_from.X, _to.X, e),
            Lerp(_from.Y, _to.Y, e),
            Lerp(_from.Z, _to.Z, e),
            Lerp(_from.Distance, _to.Distance, e),
            Lerp(_from.VerticalAngle, _to.VerticalAngle, e),
            WrapYaw(_from.HorizontalAngle + _yawDegrees * e));
    }

    /// <summary>An orbit around a fixed point by <paramref name="degrees"/> of yaw.</summary>
    public static CameraPath Orbit(CameraFrame start, double degrees)
        => new(start, start with { HorizontalAngle = WrapYaw(start.HorizontalAngle + degrees) }, degrees);

    /// <summary>
    /// A fly-over from one point to another. The camera faces the direction of travel
    /// (yaw from the heading), so the city scrolls towards the viewer.
    /// </summary>
    public static CameraPath Flyover(CameraFrame start, double toX, double toZ, double? toDistance)
    {
        double heading = Heading(start.X, start.Z, toX, toZ);
        var origin = start with { HorizontalAngle = heading };
        var destination = origin with { X = toX, Z = toZ, Distance = toDistance ?? start.Distance };
        return new CameraPath(origin, destination, 0);
    }

    /// <summary>A push-in or pull-out on a fixed point (distance changes only).</summary>
    public static CameraPath Zoom(CameraFrame start, double toDistance)
        => new(start, start with { Distance = toDistance }, 0);

    /// <summary>
    /// The camera yaw that looks along the travel direction. CS1's yaw 0 faces +z
    /// (north) and grows clockwise towards +x (east), like a compass bearing.
    /// </summary>
    public static double Heading(double fromX, double fromZ, double toX, double toZ)
    {
        double dx = toX - fromX;
        double dz = toZ - fromZ;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dz) < 1e-9)
        {
            return 0;
        }
        return WrapYaw(Math.Atan2(dx, dz) * 180.0 / Math.PI);
    }

    public static double WrapYaw(double degrees)
    {
        double wrapped = ((degrees + 180) % 360 + 360) % 360 - 180;
        return wrapped == -180 ? 180 : wrapped;
    }

    private static double ShortestTurn(double from, double to) => WrapYaw(to - from);

    private static double Ease(double t) => t * t * (3 - 2 * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
