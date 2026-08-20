namespace RapidPcUse;

internal enum CaptureTier
{
    Native = 0,
    Tier720 = 720,
    Tier900 = 900,
}

internal sealed record CaptureResolution(
    int Width,
    int Height,
    int ShortEdgeTier,
    string AspectClass,
    bool Resized);

internal static class CaptureResolutionPolicy
{
    private const double AspectMatchTolerance = 0.0075;

    // Ratios are ordered from widest to narrowest so ultrawide variants map to
    // stable, human-recognizable targets instead of an arbitrary max edge.
    private static readonly AspectRatio[] KnownRatios =
    [
        new("32:9", 32, 9),
        new("12:5", 12, 5),
        new("43:18", 43, 18),
        new("64:27", 64, 27),
        new("21:9", 21, 9),
        new("16:9", 16, 9),
        new("16:10", 16, 10),
        new("3:2", 3, 2),
        new("4:3", 4, 3),
        new("5:4", 5, 4),
        new("1:1", 1, 1),
    ];

    internal static CaptureResolution Select(int nativeWidth, int nativeHeight, CaptureTier maximumTier)
    {
        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeWidth), "Capture dimensions must be positive.");
        }

        var landscape = nativeWidth >= nativeHeight;
        var nativeShortEdge = Math.Min(nativeWidth, nativeHeight);
        var nativeLongEdge = Math.Max(nativeWidth, nativeHeight);
        var targetShortEdge = SelectShortEdge(nativeShortEdge, maximumTier);
        var aspect = (double)nativeLongEdge / nativeShortEdge;
        var knownRatio = FindKnownRatio(aspect);
        var aspectClass = knownRatio?.Name ?? $"custom-{aspect:F4}";

        if (targetShortEdge == nativeShortEdge)
        {
            return new CaptureResolution(nativeWidth, nativeHeight, nativeShortEdge, aspectClass, Resized: false);
        }

        var targetLongEdge = knownRatio is null
            ? Math.Max(targetShortEdge, (int)Math.Round(nativeLongEdge * ((double)targetShortEdge / nativeShortEdge)))
            : Math.Max(targetShortEdge, (int)Math.Round((double)targetShortEdge * knownRatio.Numerator / knownRatio.Denominator));
        var targetWidth = landscape ? targetLongEdge : targetShortEdge;
        var targetHeight = landscape ? targetShortEdge : targetLongEdge;
        return new CaptureResolution(targetWidth, targetHeight, targetShortEdge, aspectClass, Resized: true);
    }

    internal static CaptureTier ReadEnvironmentTier()
    {
        var raw = Environment.GetEnvironmentVariable("RAPID_PC_CAPTURE_TIER")?.Trim();
        return raw?.ToLowerInvariant() switch
        {
            null or "" or "900" or "900p" => CaptureTier.Tier900,
            "720" or "720p" => CaptureTier.Tier720,
            "native" or "off" or "0" => CaptureTier.Native,
            _ => throw new InvalidOperationException(
                "RAPID_PC_CAPTURE_TIER must be '900', '720', or 'native'."),
        };
    }

    private static int SelectShortEdge(int nativeShortEdge, CaptureTier maximumTier)
    {
        if (maximumTier == CaptureTier.Native)
        {
            return nativeShortEdge;
        }

        return Math.Min(nativeShortEdge, (int)maximumTier);
    }

    private static AspectRatio? FindKnownRatio(double aspect)
        => KnownRatios
            .Select(ratio => new
            {
                Ratio = ratio,
                RelativeDifference = Math.Abs(aspect - ratio.Value) / ratio.Value,
            })
            .Where(candidate => candidate.RelativeDifference <= AspectMatchTolerance)
            .OrderBy(candidate => candidate.RelativeDifference)
            .Select(candidate => candidate.Ratio)
            .FirstOrDefault();

    private sealed record AspectRatio(string Name, int Numerator, int Denominator)
    {
        internal double Value => (double)Numerator / Denominator;
    }
}
