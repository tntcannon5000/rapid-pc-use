using System.Text.Json;

namespace RapidPcUse;

internal sealed class DesktopController : IDisposable
{
    private readonly InputController _input = new();
    private readonly IScreenCaptureBackend _capture = new GdiScreenCaptureBackend();
    private readonly ControlOverlay _overlay;
    private readonly ControlSession _session;
    private readonly PhysicalEscapeHook _escapeHook;
    private long _frameSequence;
    private long _lastFrameId;
    private string? _lastTopologyKey;

    internal DesktopController()
    {
        _overlay = new ControlOverlay();
        _session = new ControlSession(_input, _overlay);
        _escapeHook = new PhysicalEscapeHook(_session);
    }

    internal Observation Observe(bool beginControl)
    {
        if (beginControl)
        {
            _session.Start();
        }

        _session.Touch();
        return CaptureAndRemember();
    }

    internal Observation? Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter)
    {
        _session.EnsureCanAct();
        ValidateFrame(frameId);
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0)
        {
            throw new ArgumentException("actions must be a non-empty array.");
        }

        if (actions.GetArrayLength() > 32)
        {
            throw new ArgumentException("A batch may contain at most 32 actions.");
        }

        var monitors = MonitorManager.GetMonitors();
        var topology = MonitorManager.GetTopologyKey(monitors);
        if (!string.Equals(topology, _lastTopologyKey, StringComparison.Ordinal))
        {
            throw new StaleFrameException("Display topology changed. Call pc_observe and use the new frame_id before acting.");
        }

        var actionIndex = 0;
        foreach (var action in actions.EnumerateArray())
        {
            try
            {
                ExecuteAction(action, monitors);
            }
            catch (UserTakeoverException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var actionType = SafeActionType(action);
                throw new PcActionException(actionIndex, actionType, SafeActionDescription(action, actionType), exception);
            }

            ThrowIfTakenOver();
            _session.Touch();
            actionIndex++;
        }

        WaitCancellable(settleMilliseconds);
        ThrowIfTakenOver();
        return observeAfter ? CaptureAndRemember() : null;
    }

    internal void Stop() => _session.Stop();

    public void Dispose()
    {
        _escapeHook.Dispose();
        _session.Dispose();
        _overlay.Dispose();
    }

    private Observation CaptureAndRemember()
    {
        var frameId = Interlocked.Increment(ref _frameSequence);
        var observation = _capture.Capture(frameId, _session.IsActive);
        _lastFrameId = observation.FrameId;
        _lastTopologyKey = observation.TopologyKey;
        ThrowIfTakenOver();
        return observation;
    }

    private void ValidateFrame(long frameId)
    {
        if (frameId != _lastFrameId || frameId <= 0)
        {
            throw new StaleFrameException($"frame_id {frameId} is stale; the latest frame_id is {_lastFrameId}. Observe again before coordinate input.");
        }
    }

    private void ExecuteAction(JsonElement action, IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each action must be an object.");
        }

        var type = RequiredString(action, "type").Trim().ToLowerInvariant();
        switch (type)
        {
            case "move":
                _input.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
                break;
            case "relative_move":
                _input.RelativeMove(RequiredInt(action, "x"), RequiredInt(action, "y"));
                break;
            case "click":
                _input.Click(
                    Display(action, monitors),
                    RequiredInt(action, "x"),
                    RequiredInt(action, "y"),
                    OptionalString(action, "button", "left"),
                    Math.Clamp(OptionalInt(action, "count", 1), 1, 3),
                    IsTakenOver);
                break;
            case "mouse_down":
                if (action.TryGetProperty("x", out _))
                {
                    _input.Move(Display(action, monitors), RequiredInt(action, "x"), RequiredInt(action, "y"));
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
                    Math.Clamp(OptionalInt(action, "duration_ms", 250), 0, 10_000),
                    OptionalString(action, "button", "left"),
                    IsTakenOver);
                break;
            case "scroll":
                {
                    MonitorDescriptor? display = null;
                    int? x = null;
                    int? y = null;
                    if (action.TryGetProperty("display_id", out _))
                    {
                        display = Display(action, monitors);
                        x = RequiredInt(action, "x");
                        y = RequiredInt(action, "y");
                    }

                    _input.Scroll(
                        display,
                        x,
                        y,
                        Math.Clamp(OptionalInt(action, "scroll_y", 0), -100, 100),
                        Math.Clamp(OptionalInt(action, "scroll_x", 0), -100, 100));
                    break;
                }
            case "type":
                _input.TypeText(
                    RequiredString(action, "text"),
                    Math.Clamp(OptionalInt(action, "interval_ms", 2), 0, 100),
                    IsTakenOver);
                break;
            case "key":
                _input.PressChord(RequiredString(action, "keys"), IsTakenOver);
                break;
            case "key_down":
                _input.KeyDown(RequiredString(action, "keys"));
                break;
            case "key_up":
                _input.KeyUp(RequiredString(action, "keys"));
                break;
            case "wait":
                WaitCancellable(Math.Clamp(RequiredInt(action, "ms"), 0, 60_000));
                break;
            default:
                throw new ArgumentException($"Unsupported action type '{type}'.");
        }
    }

    private void WaitCancellable(int milliseconds)
    {
        var remaining = milliseconds;
        while (remaining > 0)
        {
            ThrowIfTakenOver();
            var slice = Math.Min(remaining, 20);
            Thread.Sleep(slice);
            remaining -= slice;
        }
    }

    private bool IsTakenOver() => _session.WasInterrupted;

    private void ThrowIfTakenOver()
    {
        if (IsTakenOver())
        {
            throw new UserTakeoverException();
        }
    }

    private static MonitorDescriptor Display(JsonElement action, IReadOnlyList<MonitorDescriptor> monitors)
        => MonitorManager.Find(monitors, RequiredString(action, "display_id"));

    private static string RequiredString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"'{property}' must be a string.");
        }

        return element.GetString()!;
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
            _ => actionType,
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
