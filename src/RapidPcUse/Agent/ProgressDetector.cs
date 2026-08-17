using System.Security.Cryptography;
using System.Text.Json;

namespace RapidPcUse.Agent;

internal sealed record ProgressObservation(
    bool ScreenChanged,
    bool RepeatedAction,
    int ConsecutiveNoProgressTurns,
    double VisualDifference);

internal sealed class ProgressDetector
{
    private Dictionary<string, byte[]>? _frameHashes;
    private string? _lastActionSignature;
    private int _consecutiveNoProgressTurns;

    internal void AcceptInitial(Observation observation) => _frameHashes = HashFrames(observation);

    internal ProgressObservation Evaluate(Observation observation, JsonElement actions)
    {
        var hashes = HashFrames(observation);
        var visualDifference = Difference(_frameHashes, hashes);
        var screenChanged = visualDifference >= 0.015;
        var signature = ActionSignature(actions);
        var repeatedAction = string.Equals(_lastActionSignature, signature, StringComparison.Ordinal);
        _consecutiveNoProgressTurns = screenChanged || !repeatedAction ? 0 : _consecutiveNoProgressTurns + 1;
        _frameHashes = hashes;
        _lastActionSignature = signature;
        return new ProgressObservation(screenChanged, repeatedAction, _consecutiveNoProgressTurns, visualDifference);
    }

    internal void Clear()
    {
        _frameHashes = null;
        _lastActionSignature = null;
        _consecutiveNoProgressTurns = 0;
    }

    internal void ResetNoProgress() => _consecutiveNoProgressTurns = 0;

    private static Dictionary<string, byte[]> HashFrames(Observation observation)
        => observation.Frames.ToDictionary(
            frame => frame.Monitor.Id,
            frame => frame.ContentFingerprint ?? SHA256.HashData(frame.Bytes),
            StringComparer.Ordinal);

    private static double Difference(
        Dictionary<string, byte[]>? prior,
        Dictionary<string, byte[]> current)
    {
        if (prior is null || prior.Count != current.Count)
        {
            return 1;
        }

        double total = 0;
        var samples = 0;
        foreach (var pair in current)
        {
            if (!prior.TryGetValue(pair.Key, out var oldHash) || oldHash.Length != pair.Value.Length)
            {
                return 1;
            }

            for (var index = 0; index < pair.Value.Length; index++)
            {
                total += Math.Abs(pair.Value[index] - oldHash[index]) / 255d;
            }

            samples += pair.Value.Length;
        }

        return samples == 0 ? 0 : total / samples;
    }

    private static string ActionSignature(JsonElement actions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var action in actions.EnumerateArray())
        {
            AddSafeProperty(hash, action, "type");
            AddSafeProperty(hash, action, "display_id");
            AddSafeProperty(hash, action, "x");
            AddSafeProperty(hash, action, "y");
            AddSafeProperty(hash, action, "to_x");
            AddSafeProperty(hash, action, "to_y");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddSafeProperty(IncrementalHash hash, JsonElement action, string property)
    {
        if (!action.TryGetProperty(property, out var value))
        {
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(value.GetRawText());
        hash.AppendData(bytes);
    }
}
