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
    long CaptureMilliseconds,
    CaptureResolution Resolution,
    CaptureStageTimings Timings,
    byte[]? ContentFingerprint = null);

internal sealed record CaptureStageTimings(
    long SurfaceSetupMicroseconds,
    long BlitMicroseconds,
    long CursorMicroseconds,
    long MaterializeMicroseconds,
    long ResizeMicroseconds,
    long EncodeMicroseconds,
    long TotalMicroseconds);

internal sealed record ActionTiming(
    int Index,
    string Type,
    long ElapsedMicroseconds,
    int? RequestedWaitMilliseconds,
    int? TypedCodeUnits,
    int? TypeIntervalMilliseconds,
    int? PointerPacingMilliseconds);

internal sealed record DesktopActResult(
    Observation? Observation,
    IReadOnlyList<ActionTiming> Actions,
    int SettleRequestedMilliseconds,
    long SettleElapsedMicroseconds,
    DesktopActionFailure? Failure = null);

internal sealed record DesktopActionFailure(
    int ActionIndex,
    string ActionType,
    string SafeSummary,
    int CompletedActions,
    string FailureCode = "native_action_failed",
    int? NativeErrorCode = null,
    bool? TargetWithinVirtualDesktop = null);

internal sealed record ImageContextMetric(
    string DisplayId,
    int EncodedWidth,
    int EncodedHeight,
    int EncodedBytes,
    long Estimated32PixelPatches,
    bool ExactRepeatOfPrevious);

internal sealed record ContextObservationMetric(
    long FrameOrdinal,
    IReadOnlyList<ImageContextMetric> Images,
    long CumulativeImagesReturned,
    long CumulativeEncodedBytesReturned,
    long CumulativeEstimated32PixelPatches,
    long ExactRepeatImages,
    string RetentionInterpretation,
    bool ProviderCacheMetricsAvailable);

internal sealed record Observation(
    long FrameId,
    string TopologyKey,
    IReadOnlyList<ScreenFrame> Frames,
    long TotalMilliseconds,
    bool ControlActive,
    string CaptureScope = "full_desktop");

internal sealed class UserTakeoverException : Exception
{
    internal const string CanonicalMessage = "The user is now operating the PC";

    public UserTakeoverException() : base(CanonicalMessage)
    {
    }
}

internal sealed class StaleFrameException(string message) : Exception(message);

internal sealed class PcActionPlanValidationException : ArgumentException
{
    internal PcActionPlanValidationException(
        string code,
        string message,
        string? field = null,
        long? suppliedValue = null,
        long? allowedMinimum = null,
        long? allowedMaximum = null,
        int? actionIndex = null,
        string? actionType = null)
        : base(message, field)
    {
        Code = code;
        Field = field;
        SuppliedValue = suppliedValue;
        AllowedMinimum = allowedMinimum;
        AllowedMaximum = allowedMaximum;
        ActionIndex = actionIndex;
        ActionType = actionType;
        SafeMessage = message;
    }

    internal string Code { get; }
    internal string SafeMessage { get; }
    internal string? Field { get; }
    internal long? SuppliedValue { get; }
    internal long? AllowedMinimum { get; }
    internal long? AllowedMaximum { get; }
    internal int? ActionIndex { get; }
    internal string? ActionType { get; }

    internal PcActionPlanValidationException WithAction(int actionIndex, string actionType)
        => ActionIndex.HasValue
            ? this
            : new(
                Code,
                SafeMessage,
                Field,
                SuppliedValue,
                AllowedMinimum,
                AllowedMaximum,
                actionIndex,
                actionType);

    internal object SafeData() => new
    {
        code = Code,
        action_index = ActionIndex,
        action_type = ActionType,
        field = Field,
        supplied_value = SuppliedValue,
        allowed_minimum = AllowedMinimum,
        allowed_maximum = AllowedMaximum,
        no_actions_executed = true,
        frame_consumed = false,
        control_released = false,
    };
}

internal sealed class ControlSessionEndedException()
    : Exception("The desktop control session ended before the action completed.");

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

internal sealed class NativeInputStageException(string stage, Exception innerException)
    : Exception("A native input stage failed.", innerException)
{
    internal string Stage { get; } = stage;
}
