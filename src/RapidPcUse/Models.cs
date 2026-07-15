namespace RapidPcUse;

internal sealed record MonitorDescriptor(
    string Id,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

internal sealed record ScreenFrame(
    long FrameId,
    MonitorDescriptor Monitor,
    int EncodedWidth,
    int EncodedHeight,
    string MimeType,
    byte[] Bytes,
    long CaptureMilliseconds);

internal sealed record Observation(
    long FrameId,
    string TopologyKey,
    IReadOnlyList<ScreenFrame> Frames,
    long TotalMilliseconds,
    bool ControlActive);

internal sealed class UserTakeoverException : Exception
{
    internal const string CanonicalMessage = "The user is now operating the PC";

    public UserTakeoverException() : base(CanonicalMessage)
    {
    }
}

internal sealed class StaleFrameException(string message) : Exception(message);

internal sealed class PcActionException : Exception
{
    internal PcActionException(int actionIndex, string actionType, string safeDescription, Exception innerException)
        : base($"Action {actionIndex + 1} ({safeDescription}) could not be completed.", innerException)
    {
        Data["action_index"] = actionIndex + 1;
        Data["action_type"] = actionType;
        Data["action_summary"] = safeDescription;
    }
}
