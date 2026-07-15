using System.IO;
using System.Text.Json;

namespace InputApiProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        _ = Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
        try
        {
            var command = args.Length == 0 ? "snapshot" : args[0].ToLowerInvariant();
            object result = command switch
            {
                "snapshot" => Diagnostics.Capture(),
                "attempt" => Attempt(args),
                "activate" => Activate(args),
                "click-choreography" => ClickChoreography(args),
                "touch" => Touch(args),
                "uia" => Uia(args),
                "msaa" => Msaa(args),
                "message-click" => MessageClick(args),
                "matrix" => Matrix(args),
                _ => throw new ArgumentException("Usage: InputApiProbe [snapshot | activate <hwnd> | attempt <method> <x> <y> | click-choreography <control-hwnd> <target-hwnd> <x> <y> <method> | uia <hwnd> [action-name] | matrix <x> <y> [repetitions] [foreground-hwnd]]"),
            };

            if (result is MatrixResult matrix)
            {
                var directory = Path.Combine(Environment.CurrentDirectory, "artifacts");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"input-api-matrix-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(matrix, JsonOptions));
                Console.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        artifact = path,
                        matrix.Target,
                        matrix.Origin,
                        matrix.Repetitions,
                        matrix.Activation,
                        attempts = matrix.Attempts.Select(attempt => new
                        {
                            attempt.Method,
                            attempt.Call.ReturnedSuccess,
                            attempt.Call.ReturnValue,
                            attempt.Call.LastError,
                            attempt.Moved,
                            attempt.ReachedTarget,
                            attempt.Final,
                            attempt.FinalDistance,
                            attempt.MaximumMovement,
                        }),
                    },
                    JsonOptions));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static AttemptResult Attempt(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException("attempt requires <method> <x> <y>.");
        }

        return InputBackends.Run(args[1], Point(args[2], args[3]), 250);
    }

    private static MatrixResult Matrix(string[] args)
    {
        if (args.Length is < 3 or > 5)
        {
            throw new ArgumentException("matrix requires <x> <y> [repetitions] [foreground-hwnd].");
        }

        var target = Point(args[1], args[2]);
        var repetitions = args.Length >= 4 ? Math.Clamp(int.Parse(args[3]), 1, 20) : 1;
        var requestedForeground = args.Length == 5 ? ParseHandle(args[4]) : nint.Zero;
        var activation = requestedForeground == nint.Zero ? null : ActivateWindow(requestedForeground);
        var origin = Diagnostics.PhysicalCursor();
        var attempts = new List<AttemptResult>();
        foreach (var method in InputBackends.MatrixMethods)
        {
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                if (requestedForeground != nint.Zero && Native.GetForegroundWindow() != requestedForeground)
                {
                    activation = ActivateWindow(requestedForeground);
                }

                var attempt = InputBackends.Run(method, target, 250);
                attempts.Add(attempt);

                if (attempt.Moved)
                {
                    _ = InputBackends.Run(method, origin, 250);
                }
            }
        }

        return new MatrixResult(DateTimeOffset.Now, target, origin, repetitions, activation, attempts);
    }

    private static ActivationResult Activate(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException("activate requires <hwnd>.");
        }

        return ActivateWindow(ParseHandle(args[1]));
    }

    private static ClickChoreographyResult ClickChoreography(string[] args)
    {
        if (args.Length != 6)
        {
            throw new ArgumentException("click-choreography requires <control-hwnd> <target-hwnd> <x> <y> <mouse-event|sendinput-zero|sendinput-sentinel>.");
        }

        var controlWindow = ParseHandle(args[1]);
        var targetWindow = ParseHandle(args[2]);
        var target = Point(args[3], args[4]);
        var before = Diagnostics.Capture();
        var controlActivation = ActivateWindow(controlWindow);
        var positionCall = InputBackends.Execute("set-cursor", target);
        Thread.Sleep(100);
        var positioned = Diagnostics.PhysicalCursor();
        var targetActivation = ActivateWindow(targetWindow);
        var positionAfterActivation = Diagnostics.PhysicalCursor();
        var calls = InputBackends.Click(args[5]);
        Thread.Sleep(300);
        var after = Diagnostics.Capture();
        return new ClickChoreographyResult(
            args[5],
            target,
            before,
            controlActivation,
            positionCall,
            positioned,
            targetActivation,
            positionAfterActivation,
            calls,
            after);
    }

    private static UiaResult Uia(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            throw new ArgumentException("uia requires <hwnd> [action-name].");
        }

        return UiaProbe.Inspect(ParseHandle(args[1]), args.Length == 3 ? args[2] : null);
    }

    private static TouchResult Touch(string[] args)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException("touch requires <target-hwnd> <x> <y>.");
        }

        var window = ParseHandle(args[1]);
        var point = Point(args[2], args[3]);
        var activation = ActivateWindow(window);
        var before = Diagnostics.Capture();
        var calls = InputBackends.Touch(point);
        Thread.Sleep(400);
        return new TouchResult(point, activation, before, calls, Diagnostics.Capture());
    }

    private static MsaaResult Msaa(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            throw new ArgumentException("msaa requires <x> <y> [invoke].");
        }

        return MsaaProbe.Run(Point(args[1], args[2]), args.Length == 4 && bool.Parse(args[3]));
    }

    private static MessageClickResult MessageClick(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("message-click requires <x> <y>.");
        }

        var point = Point(args[1], args[2]);
        var window = Native.WindowFromPoint(new Native.Point { X = point.X, Y = point.Y });
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        var sent = Native.SendMessageTimeout(
            window,
            Native.ButtonClick,
            0,
            nint.Zero,
            Native.SendMessageAbortIfHung,
            1000,
            out var messageResult);
        var sendError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        var posted = Native.PostMessage(window, Native.ButtonClick, 0, nint.Zero);
        var postError = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return new MessageClickResult(
            point,
            Diagnostics.Hex(window),
            sent != nint.Zero,
            sendError,
            (ulong)messageResult,
            posted,
            postError);
    }

    private static ActivationResult ActivateWindow(nint window)
    {
        var before = Native.GetForegroundWindow();
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        var returned = Native.SetForegroundWindow(window);
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        var attachedForeground = false;
        var retried = false;
        var altTabAttempts = 0;
        if (!returned)
        {
            var currentThread = Native.GetCurrentThreadId();
            var foregroundThread = Native.GetWindowThreadProcessId(before, out _);
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
                attachedForeground = Native.AttachThreadInput(currentThread, foregroundThread, true);
                if (attachedForeground)
                {
                    try
                    {
                        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
                        returned = Native.SetForegroundWindow(window);
                        error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                        retried = true;
                    }
                    finally
                    {
                        _ = Native.AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }
            }
        }

        Thread.Sleep(300);
        var after = Native.GetForegroundWindow();
        while (after != window && altTabAttempts < 12)
        {
            Native.keybd_event(Native.VirtualKeyMenu, 0, 0, 0);
            Native.keybd_event(Native.VirtualKeyTab, 0, 0, 0);
            Native.keybd_event(Native.VirtualKeyTab, 0, Native.KeyEventKeyUp, 0);
            Native.keybd_event(Native.VirtualKeyMenu, 0, Native.KeyEventKeyUp, 0);
            altTabAttempts++;
            Thread.Sleep(180);
            after = Native.GetForegroundWindow();
        }

        return new ActivationResult(
            Diagnostics.Hex(window),
            Diagnostics.Hex(before),
            Diagnostics.Hex(after),
            returned,
            error,
            attachedForeground,
            retried,
            altTabAttempts,
            after == window);
    }

    private static nint ParseHandle(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? new nint(Convert.ToInt64(value[2..], 16))
            : new nint(long.Parse(value));

    private static PointSnapshot Point(string x, string y) => new(int.Parse(x), int.Parse(y));
}

internal sealed record MatrixResult(
    DateTimeOffset Timestamp,
    PointSnapshot Target,
    PointSnapshot Origin,
    int Repetitions,
    ActivationResult? Activation,
    IReadOnlyList<AttemptResult> Attempts);

internal sealed record ActivationResult(
    string RequestedWindow,
    string ForegroundBefore,
    string ForegroundAfter,
    bool ReturnedSuccess,
    int LastError,
    bool AttachedForeground,
    bool Retried,
    int AltTabAttempts,
    bool Activated);

internal sealed record ClickChoreographyResult(
    string Method,
    PointSnapshot Target,
    EnvironmentSnapshot Before,
    ActivationResult ControlActivation,
    CallResult PositionCall,
    PointSnapshot Positioned,
    ActivationResult Activation,
    PointSnapshot PositionAfterActivation,
    IReadOnlyList<CallResult> ClickCalls,
    EnvironmentSnapshot After);

internal sealed record TouchResult(
    PointSnapshot Target,
    ActivationResult Activation,
    EnvironmentSnapshot Before,
    IReadOnlyList<CallResult> Calls,
    EnvironmentSnapshot After);

internal sealed record MessageClickResult(
    PointSnapshot Point,
    string Window,
    bool SendReturned,
    int SendError,
    ulong SendResult,
    bool PostReturned,
    int PostError);
