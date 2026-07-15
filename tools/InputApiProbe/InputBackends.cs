using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputApiProbe;

internal static class InputBackends
{
    internal const nuint DriverSentinel = 0x52504355;

    internal static readonly string[] MatrixMethods =
    [
        "set-cursor",
        "set-physical",
        "sendinput-absolute-zero",
        "sendinput-absolute-sentinel",
        "sendinput-relative-zero",
        "mouse-event-absolute-zero",
        "mouse-event-relative-zero",
        "worker-set-cursor",
        "worker-input-desktop-set-cursor",
        "attach-foreground-set-cursor",
    ];

    internal static AttemptResult Run(string method, PointSnapshot target, int settleMilliseconds)
    {
        var beforeEnvironment = Diagnostics.Capture();
        var before = beforeEnvironment.PhysicalCursor;
        var call = Execute(method, target);
        var samples = new List<CursorSample>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var sampleAt in new[] { 0, 1, 5, 16, 50, 100, 250 })
        {
            var remaining = sampleAt - (int)stopwatch.ElapsedMilliseconds;
            if (remaining > 0)
            {
                Thread.Sleep(remaining);
            }

            samples.Add(new CursorSample(stopwatch.Elapsed.TotalMilliseconds, Diagnostics.PhysicalCursor()));
        }

        if (settleMilliseconds > 250)
        {
            Thread.Sleep(settleMilliseconds - 250);
        }

        var afterEnvironment = Diagnostics.Capture();
        var final = afterEnvironment.PhysicalCursor;
        var distance = Distance(final, target);
        var maximumMovement = samples
            .Select(sample => Distance(sample.Position, before))
            .DefaultIfEmpty(0)
            .Max();

        return new AttemptResult(
            method,
            target,
            before,
            final,
            call,
            samples,
            distance,
            maximumMovement,
            distance <= 1,
            maximumMovement > 1,
            beforeEnvironment,
            afterEnvironment);
    }

    internal static IReadOnlyList<CallResult> Click(string method)
    {
        return method switch
        {
            "mouse-event" => MouseEventClick(),
            "sendinput-zero" => SendInputClick(0),
            "sendinput-sentinel" => SendInputClick(DriverSentinel),
            _ => throw new ArgumentException($"Unknown click method '{method}'."),
        };
    }

    internal static IReadOnlyList<CallResult> Touch(PointSnapshot target)
    {
        var results = new List<CallResult>();
        var stopwatch = Stopwatch.StartNew();
        Marshal.SetLastPInvokeError(0);
        var initialized = Native.InitializeTouchInjection(1, Native.TouchFeedbackNone);
        var initializeError = Marshal.GetLastPInvokeError();
        results.Add(new CallResult(initialized, initialized ? 1 : 0, initializeError, stopwatch.Elapsed.TotalMilliseconds, "InitializeTouchInjection"));
        if (!initialized)
        {
            return results;
        }

        var contact = new Native.PointerTouchInfo
        {
            PointerInfo = new Native.PointerInfo
            {
                PointerType = Native.PointerInputTypeTouch,
                PointerId = 1,
                PixelLocation = new Native.Point { X = target.X, Y = target.Y },
                PointerFlags = Native.PointerFlagDown | Native.PointerFlagInRange | Native.PointerFlagInContact | Native.PointerFlagPrimary,
            },
            TouchMask = Native.TouchMaskContactArea | Native.TouchMaskOrientation | Native.TouchMaskPressure,
            Contact = new Native.Rect { Left = target.X - 2, Top = target.Y - 2, Right = target.X + 2, Bottom = target.Y + 2 },
            Orientation = 90,
            Pressure = 32000,
        };

        results.Add(InjectTouch(contact, "down"));
        Thread.Sleep(60);
        contact.PointerInfo.PointerFlags = Native.PointerFlagUp | Native.PointerFlagPrimary;
        results.Add(InjectTouch(contact, "up"));
        return results;
    }

    internal static CallResult Execute(string method, PointSnapshot target)
    {
        Marshal.SetLastPInvokeError(0);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return method switch
            {
                "set-cursor" => BooleanCall(() => Native.SetCursorPos(target.X, target.Y), stopwatch),
                "set-physical" => BooleanCall(() => Native.SetPhysicalCursorPos(target.X, target.Y), stopwatch),
                "sendinput-absolute-zero" => SendInputAbsolute(target, 0, stopwatch),
                "sendinput-absolute-sentinel" => SendInputAbsolute(target, DriverSentinel, stopwatch),
                "sendinput-relative-zero" => SendInputRelative(target, 0, stopwatch),
                "mouse-event-absolute-zero" => MouseEventAbsolute(target, 0, stopwatch),
                "mouse-event-relative-zero" => MouseEventRelative(target, 0, stopwatch),
                "worker-set-cursor" => OnWorkerThread(target, attachInputDesktop: false, stopwatch),
                "worker-input-desktop-set-cursor" => OnWorkerThread(target, attachInputDesktop: true, stopwatch),
                "attach-foreground-set-cursor" => AttachForeground(target, stopwatch),
                _ => throw new ArgumentException($"Unknown method '{method}'."),
            };
        }
        catch (Exception exception)
        {
            return new CallResult(false, -1, Marshal.GetLastPInvokeError(), stopwatch.Elapsed.TotalMilliseconds, exception.ToString());
        }
    }

    private static CallResult BooleanCall(Func<bool> action, Stopwatch stopwatch)
    {
        Marshal.SetLastPInvokeError(0);
        var returned = action();
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(returned, returned ? 1 : 0, error, stopwatch.Elapsed.TotalMilliseconds, null);
    }

    private static CallResult SendInputAbsolute(PointSnapshot target, nuint extraInfo, Stopwatch stopwatch)
    {
        var virtualLeft = Native.GetSystemMetrics(Native.SmXVirtualScreen);
        var virtualTop = Native.GetSystemMetrics(Native.SmYVirtualScreen);
        var virtualWidth = Native.GetSystemMetrics(Native.SmCxVirtualScreen);
        var virtualHeight = Native.GetSystemMetrics(Native.SmCyVirtualScreen);
        var dx = Normalize(target.X, virtualLeft, virtualWidth);
        var dy = Normalize(target.Y, virtualTop, virtualHeight);
        var input = MouseInput(dx, dy, Native.MouseEventMove | Native.MouseEventAbsolute | Native.MouseEventVirtualDesk, extraInfo);
        Marshal.SetLastPInvokeError(0);
        var sent = Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(sent == 1, sent, error, stopwatch.Elapsed.TotalMilliseconds, $"normalized=({dx},{dy})");
    }

    private static CallResult SendInputRelative(PointSnapshot target, nuint extraInfo, Stopwatch stopwatch)
    {
        var before = Diagnostics.PhysicalCursor();
        var input = MouseInput(target.X - before.X, target.Y - before.Y, Native.MouseEventMove, extraInfo);
        Marshal.SetLastPInvokeError(0);
        var sent = Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(sent == 1, sent, error, stopwatch.Elapsed.TotalMilliseconds, $"raw_delta=({target.X - before.X},{target.Y - before.Y})");
    }

    private static CallResult MouseEventAbsolute(PointSnapshot target, nuint extraInfo, Stopwatch stopwatch)
    {
        var virtualLeft = Native.GetSystemMetrics(Native.SmXVirtualScreen);
        var virtualTop = Native.GetSystemMetrics(Native.SmYVirtualScreen);
        var virtualWidth = Native.GetSystemMetrics(Native.SmCxVirtualScreen);
        var virtualHeight = Native.GetSystemMetrics(Native.SmCyVirtualScreen);
        var dx = Normalize(target.X, virtualLeft, virtualWidth);
        var dy = Normalize(target.Y, virtualTop, virtualHeight);
        Marshal.SetLastPInvokeError(0);
        Native.mouse_event(Native.MouseEventMove | Native.MouseEventAbsolute | Native.MouseEventVirtualDesk, (uint)dx, (uint)dy, 0, extraInfo);
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(true, 1, error, stopwatch.Elapsed.TotalMilliseconds, $"normalized=({dx},{dy}); void API");
    }

    private static CallResult MouseEventRelative(PointSnapshot target, nuint extraInfo, Stopwatch stopwatch)
    {
        var before = Diagnostics.PhysicalCursor();
        var dx = target.X - before.X;
        var dy = target.Y - before.Y;
        Marshal.SetLastPInvokeError(0);
        Native.mouse_event(Native.MouseEventMove, unchecked((uint)dx), unchecked((uint)dy), 0, extraInfo);
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(true, 1, error, stopwatch.Elapsed.TotalMilliseconds, $"raw_delta=({dx},{dy}); void API");
    }

    private static IReadOnlyList<CallResult> MouseEventClick()
    {
        var results = new List<CallResult>();
        foreach (var flags in new[] { Native.MouseEventLeftDown, Native.MouseEventLeftUp })
        {
            var stopwatch = Stopwatch.StartNew();
            Marshal.SetLastPInvokeError(0);
            Native.mouse_event(flags, 0, 0, 0, 0);
            var error = Marshal.GetLastPInvokeError();
            results.Add(new CallResult(true, 1, error, stopwatch.Elapsed.TotalMilliseconds, $"flags=0x{flags:X}; void API"));
            Thread.Sleep(35);
        }

        return results;
    }

    private static IReadOnlyList<CallResult> SendInputClick(nuint extraInfo)
    {
        var inputs = new[]
        {
            MouseInput(0, 0, Native.MouseEventLeftDown, extraInfo),
            MouseInput(0, 0, Native.MouseEventLeftUp, extraInfo),
        };
        var stopwatch = Stopwatch.StartNew();
        Marshal.SetLastPInvokeError(0);
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
        var error = Marshal.GetLastPInvokeError();
        return
        [
            new CallResult(sent == inputs.Length, sent, error, stopwatch.Elapsed.TotalMilliseconds, $"requested={inputs.Length}; extra_info=0x{extraInfo:X}"),
        ];
    }

    private static CallResult InjectTouch(Native.PointerTouchInfo contact, string phase)
    {
        var stopwatch = Stopwatch.StartNew();
        Marshal.SetLastPInvokeError(0);
        var returned = Native.InjectTouchInput(1, [contact]);
        var error = Marshal.GetLastPInvokeError();
        return new CallResult(returned, returned ? 1 : 0, error, stopwatch.Elapsed.TotalMilliseconds, $"touch_{phase}; struct_size={Marshal.SizeOf<Native.PointerTouchInfo>()}");
    }

    private static CallResult OnWorkerThread(PointSnapshot target, bool attachInputDesktop, Stopwatch stopwatch)
    {
        CallResult? result = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            nint desktop = nint.Zero;
            try
            {
                string? detail = null;
                if (attachInputDesktop)
                {
                    Marshal.SetLastPInvokeError(0);
                    desktop = Native.OpenInputDesktop(0, false, Native.DesktopReadObjects | Native.DesktopWriteObjects);
                    var openError = Marshal.GetLastPInvokeError();
                    if (desktop == nint.Zero)
                    {
                        result = new CallResult(false, 0, openError, stopwatch.Elapsed.TotalMilliseconds, "OpenInputDesktop failed.");
                        return;
                    }

                    Marshal.SetLastPInvokeError(0);
                    var attached = Native.SetThreadDesktop(desktop);
                    var attachError = Marshal.GetLastPInvokeError();
                    detail = $"input_desktop={Diagnostics.UserObjectName(desktop)}; set_thread_desktop={attached}; set_thread_desktop_error={attachError}";
                    if (!attached)
                    {
                        result = new CallResult(false, 0, attachError, stopwatch.Elapsed.TotalMilliseconds, detail);
                        return;
                    }
                }

                Marshal.SetLastPInvokeError(0);
                var moved = Native.SetCursorPos(target.X, target.Y);
                var error = Marshal.GetLastPInvokeError();
                result = new CallResult(moved, moved ? 1 : 0, error, stopwatch.Elapsed.TotalMilliseconds, detail);
            }
            catch (Exception exception)
            {
                result = new CallResult(false, -1, Marshal.GetLastPInvokeError(), stopwatch.Elapsed.TotalMilliseconds, exception.ToString());
            }
            finally
            {
                if (desktop != nint.Zero)
                {
                    _ = Native.CloseDesktop(desktop);
                }

                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = attachInputDesktop ? "Input desktop probe" : "Worker input probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(2)))
        {
            return new CallResult(false, -1, 1460, stopwatch.Elapsed.TotalMilliseconds, "Worker timed out.");
        }

        _ = thread.Join(TimeSpan.FromSeconds(1));
        return result ?? new CallResult(false, -1, -1, stopwatch.Elapsed.TotalMilliseconds, "Worker returned no result.");
    }

    private static CallResult AttachForeground(PointSnapshot target, Stopwatch stopwatch)
    {
        var currentThread = Native.GetCurrentThreadId();
        var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return BooleanCall(() => Native.SetCursorPos(target.X, target.Y), stopwatch) with { Detail = "No distinct foreground thread." };
        }

        Marshal.SetLastPInvokeError(0);
        var attached = Native.AttachThreadInput(currentThread, foregroundThread, true);
        var attachError = Marshal.GetLastPInvokeError();
        if (!attached)
        {
            return new CallResult(false, 0, attachError, stopwatch.Elapsed.TotalMilliseconds, "AttachThreadInput failed.");
        }

        try
        {
            var result = BooleanCall(() => Native.SetCursorPos(target.X, target.Y), stopwatch);
            return result with { Detail = $"attached_to_foreground_thread={foregroundThread}" };
        }
        finally
        {
            _ = Native.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static Native.Input MouseInput(int dx, int dy, uint flags, nuint extraInfo) => new()
    {
        Type = Native.InputMouse,
        Data = new Native.InputUnion
        {
            Mouse = new Native.MouseInput
            {
                Dx = dx,
                Dy = dy,
                Flags = flags,
                ExtraInfo = extraInfo,
            },
        },
    };

    private static int Normalize(int coordinate, int origin, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        return (int)Math.Round((coordinate - origin) * 65535d / (extent - 1));
    }

    private static double Distance(PointSnapshot left, PointSnapshot right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

internal sealed record CallResult(bool ReturnedSuccess, long ReturnValue, int LastError, double ElapsedMilliseconds, string? Detail);

internal sealed record CursorSample(double ElapsedMilliseconds, PointSnapshot Position);

internal sealed record AttemptResult(
    string Method,
    PointSnapshot Target,
    PointSnapshot Before,
    PointSnapshot Final,
    CallResult Call,
    IReadOnlyList<CursorSample> Samples,
    double FinalDistance,
    double MaximumMovement,
    bool ReachedTarget,
    bool Moved,
    EnvironmentSnapshot BeforeEnvironment,
    EnvironmentSnapshot AfterEnvironment);
