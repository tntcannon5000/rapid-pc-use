using System.Security.Cryptography;

namespace RapidPcUse;

internal sealed class ContextTelemetry
{
    private readonly Dictionary<string, byte[]> _previousImageDigests = new(StringComparer.Ordinal);
    private long _frames;
    private long _images;
    private long _encodedBytes;
    private long _estimatedPatches;
    private long _exactRepeats;

    internal ContextObservationMetric Record(Observation observation)
    {
        var imageMetrics = new List<ImageContextMetric>(observation.Frames.Count);
        foreach (var frame in observation.Frames)
        {
            var digest = SHA256.HashData(frame.Bytes);
            var exactRepeat = _previousImageDigests.TryGetValue(frame.Monitor.Id, out var previousDigest) &&
                              CryptographicOperations.FixedTimeEquals(digest, previousDigest);
            _previousImageDigests[frame.Monitor.Id] = digest;

            var patches = Estimate32PixelPatches(frame.EncodedWidth, frame.EncodedHeight);
            imageMetrics.Add(new ImageContextMetric(
                frame.Monitor.Id,
                frame.EncodedWidth,
                frame.EncodedHeight,
                frame.Bytes.Length,
                patches,
                exactRepeat));

            _images++;
            _encodedBytes += frame.Bytes.Length;
            _estimatedPatches += patches;
            if (exactRepeat)
            {
                _exactRepeats++;
            }
        }

        _frames++;
        return new ContextObservationMetric(
            _frames,
            imageMetrics,
            _images,
            _encodedBytes,
            _estimatedPatches,
            _exactRepeats,
            "Cumulative values are a conservative upper bound if the client retains every returned image; the MCP server cannot inspect client-side pruning or compaction.",
            ProviderCacheMetricsAvailable: false);
    }

    internal object Snapshot() => new
    {
        frames_returned = _frames,
        images_returned = _images,
        encoded_bytes_returned = _encodedBytes,
        estimated_32px_patches_returned = _estimatedPatches,
        exact_repeat_images = _exactRepeats,
        provider_cache_metrics_available = false,
    };

    internal void Reset()
    {
        _previousImageDigests.Clear();
        _frames = 0;
        _images = 0;
        _encodedBytes = 0;
        _estimatedPatches = 0;
        _exactRepeats = 0;
    }

    internal static long Estimate32PixelPatches(int width, int height)
        => checked((long)((width + 31) / 32) * ((height + 31) / 32));
}
