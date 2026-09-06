using System.Diagnostics;

namespace RapidPcUse;

internal static class InputTimingPolicy
{
    internal const int MinimumInterClickMilliseconds = 80;
    internal const int MinimumTypingIntervalMilliseconds = 5;
}

internal sealed class PointerClickPacer
{
    private readonly object _gate = new();
    private readonly Func<long> _timestampProvider;
    private readonly long _timestampFrequency;
    private readonly Action<int, Action> _wait;
    private long? _lastReleaseTimestamp;

    internal PointerClickPacer(
        Func<long>? timestampProvider = null,
        long? timestampFrequency = null,
        Action<int, Action>? wait = null)
    {
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        _timestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
        _wait = wait ?? WaitCancellable;
        if (_timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }
    }

    internal int BeforeClick(Action checkOperation)
    {
        long? lastReleaseTimestamp;
        lock (_gate)
        {
            lastReleaseTimestamp = _lastReleaseTimestamp;
        }

        if (!lastReleaseTimestamp.HasValue)
        {
            checkOperation();
            return 0;
        }

        var elapsedTicks = Math.Max(0, _timestampProvider() - lastReleaseTimestamp.Value);
        var elapsedMilliseconds = 1000d * elapsedTicks / _timestampFrequency;
        var remainingMilliseconds = (int)Math.Ceiling(
            InputTimingPolicy.MinimumInterClickMilliseconds - elapsedMilliseconds);
        if (remainingMilliseconds > 0)
        {
            var waitStarted = _timestampProvider();
            _wait(remainingMilliseconds, checkOperation);
            var waitedTicks = Math.Max(0, _timestampProvider() - waitStarted);
            return (int)Math.Ceiling(1000d * waitedTicks / _timestampFrequency);
        }

        checkOperation();
        return 0;
    }

    internal void MarkReleased()
    {
        lock (_gate)
        {
            _lastReleaseTimestamp = _timestampProvider();
        }
    }

    private static void WaitCancellable(int milliseconds, Action checkOperation)
    {
        var remaining = milliseconds;
        while (remaining > 0)
        {
            checkOperation();
            var slice = Math.Min(remaining, 20);
            Thread.Sleep(slice);
            remaining -= slice;
        }

        checkOperation();
    }
}
