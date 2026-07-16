namespace RapidPcUse;

internal static class SecurityLimits
{
    internal const int MaxRequestLineCharacters = 1_048_576;
    internal const int MaxActionsPerBatch = 32;
    internal const int MaxTypedCodeUnitsPerAction = 10_000;
    internal const int MaxKeyChordCharacters = 128;
    internal const int MaxWaitMilliseconds = 10_000;
    internal const int MaxDragMilliseconds = 10_000;
    internal const int MaxTypeIntervalMilliseconds = 25;
    internal const int MaxBatchMilliseconds = 30_000;
    internal const int MaxDisplays = 8;
    internal const long MaxPixelsPerDisplay = 40_000_000;
    internal const long MaxTotalCapturePixels = 100_000_000;
    internal static readonly TimeSpan MaxFrameAge = TimeSpan.FromSeconds(30);
}
