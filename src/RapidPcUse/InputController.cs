using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RapidPcUse;

internal sealed class InputController
{
    private readonly object _gate = new();
    private readonly HashSet<ushort> _heldKeys = [];
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);

    internal static void Move(MonitorDescriptor monitor, int x, int y)
    {
        var (screenX, screenY) = MapNormalizedPoint(monitor, x, y);
        if (!NativeMethods.SetCursorPos(screenX, screenY))
        {
            var nativeError = Marshal.GetLastPInvokeError();
            var hasCurrentPosition = NativeMethods.GetCursorPos(out var currentPosition);
            var nativeMessage = nativeError == 0
                ? "Windows returned no extended error code."
                : new Win32Exception(nativeError).Message;
            var exception = new Win32Exception(
                nativeError,
                $"Windows rejected a request to move the native cursor to pixel ({screenX}, {screenY}) on {monitor.DeviceName}. {nativeMessage}");
            exception.Data["display_id"] = monitor.Id;
            exception.Data["display_device"] = monitor.DeviceName;
            exception.Data["normalized_x"] = x;
            exception.Data["normalized_y"] = y;
            exception.Data["target_pixel_x"] = screenX;
            exception.Data["target_pixel_y"] = screenY;
            exception.Data["cursor_before_pixel_x"] = hasCurrentPosition ? currentPosition.X : null;
            exception.Data["cursor_before_pixel_y"] = hasCurrentPosition ? currentPosition.Y : null;
            exception.Data["virtual_desktop_left"] = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
            exception.Data["virtual_desktop_top"] = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
            exception.Data["virtual_desktop_width"] = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
            exception.Data["virtual_desktop_height"] = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
            throw exception;
        }
    }

    internal static void RelativeMove(int x, int y)
    {
        SendMouse(NativeMethods.MouseeventfMove, 0, x, y);
    }

    internal void Click(
        MonitorDescriptor monitor,
        int x,
        int y,
        string button,
        int count,
        Func<bool> isCancelled)
    {
        Move(monitor, x, y);
        for (var index = 0; index < count; index++)
        {
            ThrowIfCancelled(isCancelled);
            MouseDown(button);
            MouseUp(button);
            if (index + 1 < count)
            {
                Thread.Sleep(65);
            }
        }
    }

    internal void MouseDown(string button)
    {
        lock (_gate)
        {
            var canonical = CanonicalButton(button);
            if (_heldButtons.Add(canonical))
            {
                var (flag, data) = MouseButtonInput(canonical, down: true);
                SendMouse(flag, data);
            }
        }
    }

    internal void MouseUp(string button)
    {
        lock (_gate)
        {
            var canonical = CanonicalButton(button);
            if (_heldButtons.Remove(canonical))
            {
                var (flag, data) = MouseButtonInput(canonical, down: false);
                SendMouse(flag, data);
            }
        }
    }

    internal void Drag(
        MonitorDescriptor monitor,
        int fromX,
        int fromY,
        int toX,
        int toY,
        int durationMilliseconds,
        string button,
        Func<bool> isCancelled)
    {
        Move(monitor, fromX, fromY);
        MouseDown(button);
        try
        {
            var steps = Math.Max(1, durationMilliseconds / 5);
            for (var step = 1; step <= steps; step++)
            {
                ThrowIfCancelled(isCancelled);
                var t = (double)step / steps;
                var eased = t * t * (3 - (2 * t));
                var x = (int)Math.Round(fromX + ((toX - fromX) * eased));
                var y = (int)Math.Round(fromY + ((toY - fromY) * eased));
                Move(monitor, x, y);
                if (durationMilliseconds > 0)
                {
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            MouseUp(button);
        }
    }

    internal static void Scroll(MonitorDescriptor? monitor, int? x, int? y, int verticalTicks, int horizontalTicks)
    {
        if (monitor is not null && x.HasValue && y.HasValue)
        {
            Move(monitor, x.Value, y.Value);
        }

        if (verticalTicks != 0)
        {
            SendMouse(NativeMethods.MouseeventfWheel, unchecked((uint)(-verticalTicks * NativeMethods.WheelDelta)));
        }

        if (horizontalTicks != 0)
        {
            SendMouse(NativeMethods.MouseeventfHwheel, unchecked((uint)(horizontalTicks * NativeMethods.WheelDelta)));
        }
    }

    internal void TypeText(string text, int intervalMilliseconds, Func<bool> isCancelled)
    {
        for (var index = 0; index < text.Length; index++)
        {
            ThrowIfCancelled(isCancelled);
            var character = text[index];
            if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                continue;
            }

            switch (character)
            {
                case '\r':
                case '\n':
                    PressVirtualKey(0x0D);
                    break;
                case '\t':
                    PressVirtualKey(0x09);
                    break;
                case '\b':
                    PressVirtualKey(0x08);
                    break;
                default:
                    SendUnicode(character, down: true);
                    SendUnicode(character, down: false);
                    break;
            }

            if (intervalMilliseconds > 0)
            {
                Thread.Sleep(intervalMilliseconds);
            }
        }
    }

    internal void PressChord(string chord, Func<bool> isCancelled)
    {
        var tokens = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("key chord cannot be empty.");
        }

        var keys = tokens.Select(KeyTokenToVirtualKey).ToArray();
        var pressed = new List<ushort>(keys.Length);
        try
        {
            foreach (var key in keys)
            {
                ThrowIfCancelled(isCancelled);
                KeyDown(key);
                pressed.Add(key);
            }
        }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                KeyUp(pressed[index]);
            }
        }
    }

    internal void KeyDown(string key) => KeyDown(KeyTokenToVirtualKey(key));

    internal void KeyUp(string key) => KeyUp(KeyTokenToVirtualKey(key));

    internal void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var button in _heldButtons.ToArray())
            {
                var (flag, data) = MouseButtonInput(button, down: false);
                TrySendMouse(flag, data);
            }

            _heldButtons.Clear();
            foreach (var key in _heldKeys.Reverse().ToArray())
            {
                TrySendKeyboard(key, down: false);
            }

            _heldKeys.Clear();
        }
    }

    private void KeyDown(ushort virtualKey)
    {
        lock (_gate)
        {
            if (_heldKeys.Add(virtualKey))
            {
                SendKeyboard(virtualKey, down: true);
            }
        }
    }

    private void KeyUp(ushort virtualKey)
    {
        lock (_gate)
        {
            if (_heldKeys.Remove(virtualKey))
            {
                SendKeyboard(virtualKey, down: false);
            }
        }
    }

    private void PressVirtualKey(ushort virtualKey)
    {
        KeyDown(virtualKey);
        KeyUp(virtualKey);
    }

    private static (int X, int Y) MapNormalizedPoint(MonitorDescriptor monitor, int x, int y)
    {
        if (x is < 0 or > 1000 || y is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Screenshot coordinates must be between 0 and 1000 inclusive.");
        }

        var localX = (int)Math.Round(x * (monitor.Width - 1d) / 1000d);
        var localY = (int)Math.Round(y * (monitor.Height - 1d) / 1000d);
        return (monitor.Left + localX, monitor.Top + localY);
    }

    private static string CanonicalButton(string button) => button.Trim().ToLowerInvariant() switch
    {
        "left" or "l" => "left",
        "right" or "r" => "right",
        "middle" or "m" => "middle",
        "x1" => "x1",
        "x2" => "x2",
        _ => throw new ArgumentException($"Unsupported mouse button '{button}'."),
    };

    private static (uint Flag, uint Data) MouseButtonInput(string button, bool down) => (button, down) switch
    {
        ("left", true) => (NativeMethods.MouseeventfLeftdown, 0),
        ("left", false) => (NativeMethods.MouseeventfLeftup, 0),
        ("right", true) => (NativeMethods.MouseeventfRightdown, 0),
        ("right", false) => (NativeMethods.MouseeventfRightup, 0),
        ("middle", true) => (NativeMethods.MouseeventfMiddledown, 0),
        ("middle", false) => (NativeMethods.MouseeventfMiddleup, 0),
        ("x1", true) => (NativeMethods.MouseeventfXdown, NativeMethods.Xbutton1),
        ("x1", false) => (NativeMethods.MouseeventfXup, NativeMethods.Xbutton1),
        ("x2", true) => (NativeMethods.MouseeventfXdown, NativeMethods.Xbutton2),
        ("x2", false) => (NativeMethods.MouseeventfXup, NativeMethods.Xbutton2),
        _ => throw new ArgumentException($"Unsupported mouse button '{button}'."),
    };

    private static ushort KeyTokenToVirtualKey(string token)
    {
        var normalized = token.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0]))
        {
            return normalized[0];
        }

        if (normalized.StartsWith('F') && normalized.Length <= 3 &&
            int.TryParse(normalized.AsSpan(1), out var functionIndex) && functionIndex is >= 1 and <= 24)
        {
            return (ushort)(0x6F + functionIndex);
        }

        return normalized switch
        {
            "CTRL" or "CONTROL" or "CONTROLL" => 0xA2,
            "RCTRL" or "CONTROLR" => 0xA3,
            "SHIFT" or "SHIFTL" => 0xA0,
            "RSHIFT" or "SHIFTR" => 0xA1,
            "ALT" or "ALTL" => 0xA4,
            "RALT" or "ALTR" => 0xA5,
            "WIN" or "WINDOWS" or "SUPER" or "SUPERL" => 0x5B,
            "RWIN" or "SUPERR" => 0x5C,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "SPACE" or "SPACEBAR" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "PRINTSCREEN" => 0x2C,
            "PAUSE" => 0x13,
            "APPS" or "MENU" => 0x5D,
            "PLUS" => 0xBB,
            "MINUS" => 0xBD,
            "COMMA" => 0xBC,
            "PERIOD" or "DOT" => 0xBE,
            "SLASH" => 0xBF,
            "BACKSLASH" => 0xDC,
            "SEMICOLON" => 0xBA,
            "QUOTE" => 0xDE,
            "LBRACKET" => 0xDB,
            "RBRACKET" => 0xDD,
            "BACKTICK" or "GRAVE" => 0xC0,
            _ when token.Length == 1 => VirtualKeyForCharacter(token[0]),
            _ => throw new ArgumentException($"Unsupported key '{token}'."),
        };
    }

    private static ushort VirtualKeyForCharacter(char character)
    {
        var mapped = NativeMethods.VkKeyScan(character);
        if (mapped == -1)
        {
            throw new ArgumentException($"No virtual key mapping exists for '{character}'. Use type text instead.");
        }

        return (ushort)(mapped & 0xFF);
    }

    private static void ThrowIfCancelled(Func<bool> isCancelled)
    {
        if (isCancelled())
        {
            throw new UserTakeoverException();
        }
    }

    private static void SendUnicode(char character, bool down)
    {
        var flags = NativeMethods.KeyeventfUnicode | (down ? 0 : NativeMethods.KeyeventfKeyup);
        SendInputs([
            new NativeMethods.Input
            {
                Type = NativeMethods.InputKeyboard,
                Data = new NativeMethods.InputUnion
                {
                    Keyboard = new NativeMethods.KeyboardInput
                    {
                        ScanCode = character,
                        Flags = flags,
                        ExtraInfo = NativeMethods.InputSentinel,
                    },
                },
            },
        ]);
    }

    private static void SendKeyboard(ushort virtualKey, bool down)
    {
        SendInputs([KeyboardEvent(virtualKey, down)]);
    }

    private static void TrySendKeyboard(ushort virtualKey, bool down)
    {
        try
        {
            SendKeyboard(virtualKey, down);
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup during takeover or process shutdown.
        }
    }

    private static NativeMethods.Input KeyboardEvent(ushort virtualKey, bool down) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = down ? 0 : NativeMethods.KeyeventfKeyup,
                ExtraInfo = NativeMethods.InputSentinel,
            },
        },
    };

    private static void SendMouse(uint flags, uint data, int dx = 0, int dy = 0)
    {
        SendInputs([MouseEvent(flags, data, dx, dy)]);
    }

    private static void TrySendMouse(uint flags, uint data)
    {
        try
        {
            SendMouse(flags, data);
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup during takeover or process shutdown.
        }
    }

    private static NativeMethods.Input MouseEvent(uint flags, uint data, int dx, int dy) => new()
    {
        Type = NativeMethods.InputMouse,
        Data = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = data,
                Flags = flags,
                ExtraInfo = NativeMethods.InputSentinel,
            },
        },
    };

    private static void SendInputs(NativeMethods.Input[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputs.Length)
        {
            var nativeError = Marshal.GetLastPInvokeError();
            var nativeMessage = nativeError == 0
                ? "Windows returned no extended error code."
                : new Win32Exception(nativeError).Message;
            var exception = new Win32Exception(
                nativeError,
                $"Windows accepted {sent} of {inputs.Length} requested native input events. The target may be elevated, protected, or on a different input desktop. {nativeMessage}");
            exception.Data["requested_input_events"] = inputs.Length;
            exception.Data["accepted_input_events"] = sent;
            throw exception;
        }
    }
}
