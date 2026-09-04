using System.Diagnostics;
using System.Text.Json;
using RapidPcUse.Agent;

namespace RapidPcUse;

internal sealed class DesktopController : IPcDesktop, IDisposable
{
    private const double SettledFrameDifferenceThreshold = 0.001;
    private static readonly int[] RepaintBackoffMilliseconds = [10, 20, 40, 80, 160];
    private readonly InputController _input = new();
    private readonly IScreenCaptureBackend _capture;
    private readonly TimeProvider _timeProvider;
    private readonly ControlOverlay _overlay;
    private readonly ControlSession _session;
    private readonly PhysicalEscapeHook _escapeHook;
    private long _frameSequence;
    private long _lastFrameId;
    private DateTimeOffset _lastFrameTimestampUtc;
    private string? _lastTopologyKey;
    private Dictionary<string, byte[]>? _lastFrameFingerprints;
    private IReadOnlyList<MonitorDescriptor>? _lastActionSurfaces;
    private bool _captureActiveWindow;

    internal DesktopController(IScreenCaptureBackend? capture = null, TimeProvider? timeProvider = null)
    {
        _capture = capture ?? new GdiScreenCaptureBackend();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _overlay = new ControlOverlay();
        _session = new ControlSession(_input, _overlay);
        _escapeHook = new PhysicalEscapeHook(_session);
    }

    internal Observation Observe(bool beginControl)
        => ObserveCore(beginControl, captureActiveWindow: false);

    internal Observation ObserveActiveWindow(bool beginControl)
        => ObserveCore(beginControl, captureActiveWindow: true);

    private Observation ObserveCore(bool beginControl, bool captureActiveWindow)
    {
        if (!beginControl)
        {
            throw new ArgumentException("Screen capture requires begin_control=true so the user-visible control cue remains present.");
        }

        _session.Start();
        _captureActiveWindow = captureActiveWindow;
        var operation = _session.BeginOperation();
        Action checkOperation = () => _session.ThrowIfCannotContinue(operation);
        return CaptureAndRemember(checkOperation);
    }

    internal DesktopActResult Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter)
    {
        var monitors = MonitorManager.GetMonitors();
        var actionSurfaces = _lastActionSurfaces ?? monitors;
        ValidateActionPlan(actions, settleMilliseconds, actionSurfaces);
        ValidateFrame(frameId);

        var operation = _session.BeginOperation();
        Action checkOperation = () => _session.ThrowIfCannotContinue(operation);
        checkOperation();

        var topology = MonitorManager.GetTopologyKey(monitors);
        if (!string.Equals(topology, _lastTopologyKey, StringComparison.Ordinal))
        {
            InvalidateFrame();
            throw new StaleFrameException("Display topology changed. Call pc_observe and use the new frame_id before acting.");
        }

        var priorFingerprints = _lastFrameFingerprints;
        // A frame authorizes one action batch only. Consume it before native input starts.
        InvalidateFrame();
        var actionIndex = 0;
        var actionTimings = new List<ActionTiming>(actions.GetArrayLength());
        DesktopActionFailure? failure = null;
        foreach (var action in actions.EnumerateArray())
        {
            var actionTimestamp = Stopwatch.GetTimestamp();
            var pointerPacingMilliseconds = 0;
            try
            {
                checkOperation();
                pointerPacingMilliseconds = ExecuteAction(action, actionSurfaces, checkOperation);
                checkOperation();
            }
            catch (UserTakeoverException)
            {
                throw;
            }
            catch (ControlSessionEndedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var actionType = SafeActionType(action);
                var wrapped = new PcActionException(
                    actionIndex,
                    actionType,
                    SafeActionDescription(action, actionType),
                    exception);
                failure = new DesktopActionFailure(
                    actionIndex + 1,
                    actionType,
                    wrapped.Message,
                    actionIndex);
                _input.ReleaseAll();
                break;
            }

            actionTimings.Add(CreateActionTiming(actionIndex, action, actionTimestamp, pointerPacingMilliseconds));
            _session.Touch();
            actionIndex++;
        }

        var settleTimestamp = Stopwatch.GetTimestamp();
        Observation? observation = null;
        // A partial batch must always return visible state, even when the
        // caller originally opted out of observation, so it can replan safely.
        if (observeAfter || failure is not null)
        {
            observation = CaptureWhenChanged(priorFingerprints, settleMilliseconds, checkOperation);
        }
        else
        {
            WaitCancellable(settleMilliseconds, checkOperation);
        }

        var settleElapsedMicroseconds = ElapsedMicroseconds(settleTimestamp);
        return new DesktopActResult(
            observation,
            actionTimings,
            settleMilliseconds,
            settleElapsedMicroseconds,
            failure);
    }

    internal void Stop()
    {
        InvalidateFrame();
        _session.Stop();
    }

    public CancellationToken ControlCancellationToken => _session.ControlCancellationToken;

    public void ThrowIfControlLost() => _session.ThrowIfControlLost();

    Observation IPcDesktop.Observe(bool beginControl) => Observe(beginControl);

    Observation IPcDesktop.ObserveActiveWindow(bool beginControl) => ObserveActiveWindow(beginControl);

    DesktopActResult IPcDesktop.Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter)
        => Act(frameId, actions, settleMilliseconds, observeAfter);

    void IPcDesktop.Stop() => Stop();

    public void Dispose()
    {
        InvalidateFrame();
        _escapeHook.Dispose();
        _session.Dispose();
        _overlay.Dispose();
    }

    internal static void ValidateActionPlan(
        JsonElement actions,
        int settleMilliseconds,
        IReadOnlyList<MonitorDescriptor>? monitors = null)
    {
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0)
        {
            throw InvalidPlan("invalid_actions", "actions must be a non-empty array.", "actions");
        }

        if (actions.GetArrayLength() > SecurityLimits.MaxActionsPerBatch)
        {
            throw new PcActionPlanValidationException(
                "too_many_actions",
                $"A batch may contain at most {SecurityLimits.MaxActionsPerBatch} actions.",
                "actions",
                actions.GetArrayLength(),
                1,
                SecurityLimits.MaxActionsPerBatch);
        }

        RequireRange(settleMilliseconds, 0, SecurityLimits.MaxWaitMilliseconds, "settle_ms");
        long estimatedMilliseconds = settleMilliseconds;
        var pointerActivations = 0;
        var validationActionIndex = 0;
        foreach (var action in actions.EnumerateArray())
        {
            var actionType = SafeActionType(action);
            try
            {
                if (action.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidPlan("invalid_action", "Each action must be an object.");
                }

                var type = RequiredBoundedString(action, "type", 32).Trim().ToLowerInvariant();
                actionType = type;
                switch (type)
                {
                    case "move":
                        ValidateDisplayPoint(action, "x", "y");
                        ValidateDisplay(action, monitors);
                        break;
                    case "relative_move":
                        RequireRange(RequiredInt(action, "x"), -32_768, 32_768, "x");
                        RequireRange(RequiredInt(action, "y"), -32_768, 32_768, "y");
                        break;
                    case "click":
                    case "double_click":
                        ValidateDisplayPoint(action, "x", "y");
                        ValidateDisplay(action, monitors);
                        ValidateOptionalButton(action);
                        var count = type == "double_click"
                            ? 2
                            : OptionalBoundedInt(action, "count", 1, 1, 3);
                        pointerActivations += count;
                        break;
                    case "mouse_down":
                        if (action.TryGetProperty("x", out _))
                        {
                            ValidateDisplayPoint(action, "x", "y");
                            ValidateDisplay(action, monitors);
                        }

                        ValidateOptionalButton(action);
                        pointerActivations++;
                        break;
                    case "mouse_up":
                        ValidateOptionalButton(action);
                        break;
                    case "drag":
                        ValidateDisplayPoint(action, "x", "y");
                        ValidateDisplay(action, monitors);
                        RequireRange(RequiredInt(action, "to_x"), 0, 1000, "to_x");
                        RequireRange(RequiredInt(action, "to_y"), 0, 1000, "to_y");
                        ValidateOptionalButton(action);
                        estimatedMilliseconds += OptionalBoundedInt(
                            action,
                            "duration_ms",
                            250,
                            0,
                            SecurityLimits.MaxDragMilliseconds);
                        pointerActivations++;
                        break;
                    case "scroll":
                        if (action.TryGetProperty("display_id", out _))
                        {
                            ValidateDisplayPoint(action, "x", "y");
                            ValidateDisplay(action, monitors);
                        }

                        _ = OptionalBoundedInt(
                            action,
                            "scroll_y",
                            0,
                            SecurityLimits.MinScrollDeltaPerAction,
                            SecurityLimits.MaxScrollDeltaPerAction);
                        _ = OptionalBoundedInt(
                            action,
                            "scroll_x",
                            0,
                            SecurityLimits.MinScrollDeltaPerAction,
                            SecurityLimits.MaxScrollDeltaPerAction);
                        break;
                    case "type":
                        var text = RequiredBoundedString(action, "text", SecurityLimits.MaxTypedCodeUnitsPerAction);
                        var interval = OptionalBoundedInt(
                            action,
                            "interval_ms",
                            0,
                            0,
                            SecurityLimits.MaxTypeIntervalMilliseconds);
                        estimatedMilliseconds += (long)text.Length * interval;
                        break;
                    case "key":
                        ValidateKey(action, chord: true);
                        break;
                    case "key_down":
                    case "key_up":
                        ValidateKey(action, chord: false);
                        break;
                    case "wait":
                        estimatedMilliseconds += RequireRange(
                            RequiredInt(action, "ms"),
                            0,
                            SecurityLimits.MaxWaitMilliseconds,
                            "ms");
                        break;
                    default:
                        throw InvalidPlan("unsupported_action_type", "Unsupported action type.", "type");
                }

                // Conservatively budget the full floor for every activation;
                // the first may follow a click from the preceding batch.
                estimatedMilliseconds += (long)pointerActivations * InputTimingPolicy.MinimumInterClickMilliseconds;
                pointerActivations = 0;

                if (estimatedMilliseconds > SecurityLimits.MaxBatchMilliseconds)
                {
                    throw new PcActionPlanValidationException(
                        "batch_duration_exceeded",
                        $"The batch may take at most {SecurityLimits.MaxBatchMilliseconds} milliseconds.",
                        allowedMinimum: 0,
                        allowedMaximum: SecurityLimits.MaxBatchMilliseconds);
                }
            }
            catch (PcActionPlanValidationException exception)
            {
                throw exception.WithAction(validationActionIndex + 1, actionType);
            }

            validationActionIndex++;
        }
    }

    private Observation CaptureAndRemember(Action checkOperation)
    {
        checkOperation();
        var frameId = Interlocked.Increment(ref _frameSequence);
        var observation = _captureActiveWindow
            ? _capture.CaptureActiveWindow(frameId, _session.IsActive)
            : _capture.Capture(frameId, _session.IsActive);
        checkOperation();
        _lastFrameId = observation.FrameId;
        _lastFrameTimestampUtc = _timeProvider.GetUtcNow();
        _lastTopologyKey = observation.TopologyKey;
        _lastActionSurfaces = observation.Frames.Select(frame => frame.Monitor).ToArray();
        _lastFrameFingerprints = observation.Frames.ToDictionary(
            frame => frame.Monitor.Id,
            frame => frame.ContentFingerprint ?? System.Security.Cryptography.SHA256.HashData(frame.Bytes),
            StringComparer.Ordinal);
        _session.Touch();
        return observation;
    }

    private Observation CaptureWhenChanged(
        IReadOnlyDictionary<string, byte[]>? prior,
        int initialDelayMilliseconds,
        Action checkOperation)
    {
        WaitCancellable(initialDelayMilliseconds, checkOperation);
        var observation = CaptureAndRemember(checkOperation);
        if (prior is null || FingerprintsMeaningfullyChanged(prior, _lastFrameFingerprints))
        {
            return observation;
        }

        // Short exponential backoffs avoid taxing already-painted applications
        // while ignoring tiny focus/caret differences from an unfinished repaint.
        foreach (var delay in RepaintBackoffMilliseconds)
        {
            WaitCancellable(delay, checkOperation);
            observation = CaptureAndRemember(checkOperation);
            if (FingerprintsMeaningfullyChanged(prior, _lastFrameFingerprints))
            {
                break;
            }
        }

        return observation;
    }

    private static bool FingerprintsMeaningfullyChanged(
        IReadOnlyDictionary<string, byte[]> prior,
        Dictionary<string, byte[]>? current)
    {
        if (current is null || prior.Count != current.Count)
        {
            return true;
        }

        double difference = 0;
        var samples = 0;
        foreach (var pair in prior)
        {
            if (!current.TryGetValue(pair.Key, out var candidate) ||
                pair.Value.Length != candidate.Length)
            {
                return true;
            }

            for (var index = 0; index < pair.Value.Length; index++)
            {
                difference += Math.Abs(pair.Value[index] - candidate[index]) / 255d;
            }

            samples += pair.Value.Length;
        }

        return samples > 0 && difference / samples >= SettledFrameDifferenceThreshold;
    }

    private void ValidateFrame(long frameId)
    {
        if (frameId != _lastFrameId || frameId <= 0)
        {
            throw new StaleFrameException("The frame is stale or has already been used. Observe again before coordinate input.");
        }

        if (_timeProvider.GetUtcNow() - _lastFrameTimestampUtc > SecurityLimits.MaxFrameAge)
        {
            InvalidateFrame();
            throw new StaleFrameException("The frame expired. Observe again before coordinate input.");
        }
    }

    private void InvalidateFrame()
    {
        _lastFrameId = 0;
        _lastFrameTimestampUtc = default;
        _lastTopologyKey = null;
        _lastFrameFingerprints = null;
        _lastActionSurfaces = null;
    }

    private int ExecuteAction(
        JsonElement action,
        IReadOnlyList<MonitorDescriptor> monitors,
        Action checkOperation)
    {
        var type = RequiredBoundedString(action, "type", 32).Trim().ToLowerInvariant();
        switch (type)
        {
            case "move":
                InputController.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
                return 0;
            case "relative_move":
                InputController.RelativeMove(RequiredInt(action, "x"), RequiredInt(action, "y"));
                return 0;
            case "click":
            case "double_click":
                return _input.Click(
                    Display(action, monitors),
                    RequiredInt(action, "x"),
                    RequiredInt(action, "y"),
                    OptionalString(action, "button", "left"),
                    type == "double_click" ? 2 : OptionalInt(action, "count", 1),
                    checkOperation);
            case "mouse_down":
                if (action.TryGetProperty("x", out _))
                {
                    InputController.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
                }

                return _input.BeginPointerActivation(OptionalString(action, "button", "left"), checkOperation);
            case "mouse_up":
                _input.EndPointerActivation(OptionalString(action, "button", "left"));
                return 0;
            case "drag":
                return _input.Drag(
                    Display(action, monitors),
                    RequiredInt(action, "x"),
                    RequiredInt(action, "y"),
                    RequiredInt(action, "to_x"),
                    RequiredInt(action, "to_y"),
                    OptionalInt(action, "duration_ms", 250),
                    OptionalString(action, "button", "left"),
                    checkOperation);
            case "scroll":
                MonitorDescriptor? display = null;
                int? x = null;
                int? y = null;
                if (action.TryGetProperty("display_id", out _))
                {
                    display = Display(action, monitors);
                    x = RequiredInt(action, "x");
                    y = RequiredInt(action, "y");
                }

                InputController.Scroll(
                    display,
                    x,
                    y,
                    ScrollTicks(OptionalInt(action, "scroll_y", 0)),
                    ScrollTicks(OptionalInt(action, "scroll_x", 0)));
                return 0;
            case "type":
                InputController.TypeText(
                    RequiredBoundedString(action, "text", SecurityLimits.MaxTypedCodeUnitsPerAction),
                    OptionalInt(action, "interval_ms", 0),
                    checkOperation);
                return 0;
            case "key":
                _input.PressChord(
                    RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters),
                    checkOperation);
                return 0;
            case "key_down":
                _input.KeyDown(RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters));
                return 0;
            case "key_up":
                _input.KeyUp(RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters));
                return 0;
            case "wait":
                WaitCancellable(RequiredInt(action, "ms"), checkOperation);
                return 0;
            default:
                throw new ArgumentException("Unsupported action type.");
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

    private static ActionTiming CreateActionTiming(
        int actionIndex,
        JsonElement action,
        long startTimestamp,
        int pointerPacingMilliseconds)
    {
        var type = SafeActionType(action);
        return new ActionTiming(
            actionIndex + 1,
            type,
            ElapsedMicroseconds(startTimestamp),
            type == "wait" ? SafeInteger(action, "ms") : null,
            type == "type" ? SafeString(action, "text")?.Length : null,
            type == "type" ? SafeInteger(action, "interval_ms") ?? 0 : null,
            pointerPacingMilliseconds > 0 ? pointerPacingMilliseconds : null);
    }

    private static long ElapsedMicroseconds(long startTimestamp)
        => (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);

    private static void ValidateDisplayPoint(JsonElement action, string xProperty, string yProperty)
    {
        _ = RequiredBoundedString(action, "display_id", 128);
        RequireRange(RequiredInt(action, xProperty), 0, 1000, xProperty);
        RequireRange(RequiredInt(action, yProperty), 0, 1000, yProperty);
    }

    private static MonitorDescriptor Display(JsonElement action, IReadOnlyList<MonitorDescriptor> monitors)
        => MonitorManager.Find(monitors, RequiredBoundedString(action, "display_id", 128));

    private static string RequiredBoundedString(JsonElement value, string property, int maximumLength)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw InvalidPlan("invalid_field_type", $"'{property}' must be a string.", property);
        }

        var result = element.GetString()!;
        if (result.Length > maximumLength)
        {
            throw new PcActionPlanValidationException(
                "string_too_long",
                $"'{property}' exceeds its maximum length of {maximumLength} characters.",
                property,
                result.Length,
                0,
                maximumLength);
        }

        return result;
    }

    private static int RequiredInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || !element.TryGetInt32(out var result))
        {
            throw InvalidPlan("invalid_field_type", $"'{property}' must be an integer.", property);
        }

        return result;
    }

    private static int OptionalInt(JsonElement value, string property, int fallback)
        => value.TryGetProperty(property, out var element) && element.TryGetInt32(out var result) ? result : fallback;

    private static int OptionalBoundedInt(
        JsonElement value,
        string property,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!value.TryGetProperty(property, out var element))
        {
            return fallback;
        }

        if (!element.TryGetInt32(out var result))
        {
            throw InvalidPlan("invalid_field_type", $"'{property}' must be an integer.", property);
        }

        return RequireRange(result, minimum, maximum, property);
    }

    private static int RequireRange(int value, int minimum, int maximum, string property)
    {
        if (value < minimum || value > maximum)
        {
            throw new PcActionPlanValidationException(
                "numeric_out_of_range",
                $"'{property}' must be between {minimum} and {maximum}.",
                property,
                value,
                minimum,
                maximum);
        }

        return value;
    }

    private static void ValidateOptionalString(JsonElement value, string property, int maximumLength)
    {
        if (value.TryGetProperty(property, out _))
        {
            _ = RequiredBoundedString(value, property, maximumLength);
        }
    }

    private static void ValidateOptionalButton(JsonElement value)
    {
        if (!value.TryGetProperty("button", out _))
        {
            return;
        }

        try
        {
            _ = InputController.CanonicalButton(RequiredBoundedString(value, "button", 16));
        }
        catch (ArgumentException)
        {
            throw InvalidPlan("unsupported_button", "'button' must be left, right, middle, x1, or x2.", "button");
        }
    }

    private static void ValidateDisplay(JsonElement action, IReadOnlyList<MonitorDescriptor>? monitors)
    {
        if (monitors is null)
        {
            return;
        }

        var displayId = RequiredBoundedString(action, "display_id", 128);
        if (!monitors.Any(monitor => string.Equals(monitor.Id, displayId, StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidPlan("unknown_display", "'display_id' is not present in the current display manifest.", "display_id");
        }
    }

    private static void ValidateKey(JsonElement action, bool chord)
    {
        var keys = RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters);
        try
        {
            if (chord)
            {
                InputController.ValidateChord(keys);
            }
            else
            {
                InputController.ValidateKey(keys);
            }
        }
        catch (ArgumentException)
        {
            throw InvalidPlan("unsupported_key", "'keys' contains a key name the Windows input adapter does not support.", "keys");
        }
    }

    internal static int ScrollTicks(int delta)
    {
        if (delta == 0)
        {
            return 0;
        }

        var ticks = Math.Max(1, Math.Abs((int)Math.Round(
            (double)delta / SecurityLimits.ScrollDeltaPerWheelTick,
            MidpointRounding.AwayFromZero)));
        return Math.Sign(delta) * Math.Min(ticks, SecurityLimits.MaxWheelTicksPerAction);
    }

    private static PcActionPlanValidationException InvalidPlan(string code, string message, string? field = null)
        => new(code, message, field);

    private static string OptionalString(JsonElement value, string property, string fallback)
        => value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static string SafeActionType(JsonElement action)
        => action.ValueKind == JsonValueKind.Object &&
           action.TryGetProperty("type", out var type) &&
           type.ValueKind == JsonValueKind.String
            ? type.GetString()?.Trim().ToLowerInvariant() ?? "unknown"
            : "unknown";

    private static string SafeActionDescription(JsonElement action, string actionType)
    {
        var display = SafeString(action, "display_id");
        var x = SafeInteger(action, "x");
        var y = SafeInteger(action, "y");
        return actionType switch
        {
            "move" or "click" or "double_click" => $"{actionType} on {display ?? "an unknown display"} at normalized ({x?.ToString() ?? "?"}, {y?.ToString() ?? "?"})",
            "drag" => $"drag on {display ?? "an unknown display"} from normalized ({x?.ToString() ?? "?"}, {y?.ToString() ?? "?"}) to ({SafeInteger(action, "to_x")?.ToString() ?? "?"}, {SafeInteger(action, "to_y")?.ToString() ?? "?"})",
            "scroll" => $"scroll{(display is null ? string.Empty : $" on {display}")}",
            "relative_move" => $"relative cursor move ({x?.ToString() ?? "?"}, {y?.ToString() ?? "?"})",
            "mouse_down" or "mouse_up" => actionType.Replace('_', ' '),
            "type" => $"type {SafeString(action, "text")?.Length ?? 0} UTF-16 code units (content omitted)",
            "key" or "key_down" or "key_up" => $"{actionType.Replace('_', ' ')} (literal key value omitted)",
            "wait" => $"wait {SafeInteger(action, "ms")?.ToString() ?? "?"} ms",
            _ => "desktop action",
        };
    }

    private static string? SafeString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object &&
           value.TryGetProperty(property, out var element) &&
           element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? SafeInteger(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object &&
           value.TryGetProperty(property, out var element) &&
           element.TryGetInt32(out var result)
            ? result
            : null;
}
