using System.Text.Json;

namespace RapidPcUse;

internal sealed class DesktopController : IDisposable
{
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

    internal DesktopController(IScreenCaptureBackend? capture = null, TimeProvider? timeProvider = null)
    {
        _capture = capture ?? new GdiScreenCaptureBackend();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _overlay = new ControlOverlay();
        _session = new ControlSession(_input, _overlay);
        _escapeHook = new PhysicalEscapeHook(_session);
    }

    internal Observation Observe(bool beginControl)
    {
        if (!beginControl)
        {
            throw new ArgumentException("Screen capture requires begin_control=true so the user-visible control cue remains present.");
        }

        _session.Start();
        var operation = _session.BeginOperation();
        Action checkOperation = () => _session.ThrowIfCannotContinue(operation);
        return CaptureAndRemember(checkOperation);
    }

    internal Observation? Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter)
    {
        ValidateActionPlan(actions, settleMilliseconds);
        ValidateFrame(frameId);

        var operation = _session.BeginOperation();
        Action checkOperation = () => _session.ThrowIfCannotContinue(operation);
        checkOperation();

        var monitors = MonitorManager.GetMonitors();
        var topology = MonitorManager.GetTopologyKey(monitors);
        if (!string.Equals(topology, _lastTopologyKey, StringComparison.Ordinal))
        {
            InvalidateFrame();
            throw new StaleFrameException("Display topology changed. Call pc_observe and use the new frame_id before acting.");
        }

        // A frame authorizes one action batch only. Consume it before native input starts.
        InvalidateFrame();
        var actionIndex = 0;
        foreach (var action in actions.EnumerateArray())
        {
            try
            {
                checkOperation();
                ExecuteAction(action, monitors, checkOperation);
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
                throw new PcActionException(actionIndex, actionType, SafeActionDescription(action, actionType), exception);
            }

            _session.Touch();
            actionIndex++;
        }

        WaitCancellable(settleMilliseconds, checkOperation);
        return observeAfter ? CaptureAndRemember(checkOperation) : null;
    }

    internal void Stop()
    {
        InvalidateFrame();
        _session.Stop();
    }

    public void Dispose()
    {
        InvalidateFrame();
        _escapeHook.Dispose();
        _session.Dispose();
        _overlay.Dispose();
    }

    internal static void ValidateActionPlan(JsonElement actions, int settleMilliseconds)
    {
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0)
        {
            throw new ArgumentException("actions must be a non-empty array.");
        }

        if (actions.GetArrayLength() > SecurityLimits.MaxActionsPerBatch)
        {
            throw new ArgumentException($"A batch may contain at most {SecurityLimits.MaxActionsPerBatch} actions.");
        }

        RequireRange(settleMilliseconds, 0, SecurityLimits.MaxWaitMilliseconds, "settle_ms");
        long estimatedMilliseconds = settleMilliseconds;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Each action must be an object.");
            }

            var type = RequiredBoundedString(action, "type", 32).Trim().ToLowerInvariant();
            switch (type)
            {
                case "move":
                    ValidateDisplayPoint(action, "x", "y");
                    break;
                case "relative_move":
                    RequireRange(RequiredInt(action, "x"), -32_768, 32_768, "x");
                    RequireRange(RequiredInt(action, "y"), -32_768, 32_768, "y");
                    break;
                case "click":
                    ValidateDisplayPoint(action, "x", "y");
                    ValidateOptionalString(action, "button", 16);
                    var count = OptionalBoundedInt(action, "count", 1, 1, 3);
                    estimatedMilliseconds += (count - 1L) * 65L;
                    break;
                case "mouse_down":
                    if (action.TryGetProperty("x", out _))
                    {
                        ValidateDisplayPoint(action, "x", "y");
                    }

                    ValidateOptionalString(action, "button", 16);
                    break;
                case "mouse_up":
                    ValidateOptionalString(action, "button", 16);
                    break;
                case "drag":
                    ValidateDisplayPoint(action, "x", "y");
                    RequireRange(RequiredInt(action, "to_x"), 0, 1000, "to_x");
                    RequireRange(RequiredInt(action, "to_y"), 0, 1000, "to_y");
                    ValidateOptionalString(action, "button", 16);
                    estimatedMilliseconds += OptionalBoundedInt(
                        action,
                        "duration_ms",
                        250,
                        0,
                        SecurityLimits.MaxDragMilliseconds);
                    break;
                case "scroll":
                    if (action.TryGetProperty("display_id", out _))
                    {
                        ValidateDisplayPoint(action, "x", "y");
                    }

                    _ = OptionalBoundedInt(action, "scroll_y", 0, -100, 100);
                    _ = OptionalBoundedInt(action, "scroll_x", 0, -100, 100);
                    break;
                case "type":
                    var text = RequiredBoundedString(action, "text", SecurityLimits.MaxTypedCodeUnitsPerAction);
                    var interval = OptionalBoundedInt(
                        action,
                        "interval_ms",
                        2,
                        0,
                        SecurityLimits.MaxTypeIntervalMilliseconds);
                    estimatedMilliseconds += (long)text.Length * interval;
                    break;
                case "key":
                case "key_down":
                case "key_up":
                    _ = RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters);
                    break;
                case "wait":
                    estimatedMilliseconds += RequireRange(
                        RequiredInt(action, "ms"),
                        0,
                        SecurityLimits.MaxWaitMilliseconds,
                        "ms");
                    break;
                default:
                    throw new ArgumentException("Unsupported action type.");
            }

            if (estimatedMilliseconds > SecurityLimits.MaxBatchMilliseconds)
            {
                throw new ArgumentException($"The batch may take at most {SecurityLimits.MaxBatchMilliseconds} milliseconds.");
            }
        }
    }

    private Observation CaptureAndRemember(Action checkOperation)
    {
        checkOperation();
        var frameId = Interlocked.Increment(ref _frameSequence);
        var observation = _capture.Capture(frameId, _session.IsActive);
        checkOperation();
        _lastFrameId = observation.FrameId;
        _lastFrameTimestampUtc = _timeProvider.GetUtcNow();
        _lastTopologyKey = observation.TopologyKey;
        _session.Touch();
        return observation;
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
    }

    private void ExecuteAction(
        JsonElement action,
        IReadOnlyList<MonitorDescriptor> monitors,
        Action checkOperation)
    {
        var type = RequiredBoundedString(action, "type", 32).Trim().ToLowerInvariant();
        switch (type)
        {
            case "move":
                InputController.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
                break;
            case "relative_move":
                InputController.RelativeMove(RequiredInt(action, "x"), RequiredInt(action, "y"));
                break;
            case "click":
                _input.Click(
                    Display(action, monitors),
                    RequiredInt(action, "x"),
                    RequiredInt(action, "y"),
                    OptionalString(action, "button", "left"),
                    OptionalInt(action, "count", 1),
                    checkOperation);
                break;
            case "mouse_down":
                if (action.TryGetProperty("x", out _))
                {
                    InputController.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
                }

                _input.MouseDown(OptionalString(action, "button", "left"));
                break;
            case "mouse_up":
                _input.MouseUp(OptionalString(action, "button", "left"));
                break;
            case "drag":
                _input.Drag(
                    Display(action, monitors),
                    RequiredInt(action, "x"),
                    RequiredInt(action, "y"),
                    RequiredInt(action, "to_x"),
                    RequiredInt(action, "to_y"),
                    OptionalInt(action, "duration_ms", 250),
                    OptionalString(action, "button", "left"),
                    checkOperation);
                break;
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
                    OptionalInt(action, "scroll_y", 0),
                    OptionalInt(action, "scroll_x", 0));
                break;
            case "type":
                _input.TypeText(
                    RequiredBoundedString(action, "text", SecurityLimits.MaxTypedCodeUnitsPerAction),
                    OptionalInt(action, "interval_ms", 2),
                    checkOperation);
                break;
            case "key":
                _input.PressChord(
                    RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters),
                    checkOperation);
                break;
            case "key_down":
                _input.KeyDown(RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters));
                break;
            case "key_up":
                _input.KeyUp(RequiredBoundedString(action, "keys", SecurityLimits.MaxKeyChordCharacters));
                break;
            case "wait":
                WaitCancellable(RequiredInt(action, "ms"), checkOperation);
                break;
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
            throw new ArgumentException($"'{property}' must be a string.");
        }

        var result = element.GetString()!;
        if (result.Length > maximumLength)
        {
            throw new ArgumentException($"'{property}' exceeds its maximum length of {maximumLength} characters.");
        }

        return result;
    }

    private static int RequiredInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || !element.TryGetInt32(out var result))
        {
            throw new ArgumentException($"'{property}' must be an integer.");
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
            throw new ArgumentException($"'{property}' must be an integer.");
        }

        return RequireRange(result, minimum, maximum, property);
    }

    private static int RequireRange(int value, int minimum, int maximum, string property)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(property, $"'{property}' must be between {minimum} and {maximum}.");
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
            "move" or "click" => $"{actionType} on {display ?? "an unknown display"} at normalized ({x?.ToString() ?? "?"}, {y?.ToString() ?? "?"})",
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
