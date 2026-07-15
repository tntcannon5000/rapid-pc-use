using System.Collections.Concurrent;
using System.ComponentModel;

namespace RapidPcUse;

internal sealed class ControlOverlay : IDisposable
{
    private const uint OverlayCommandMessage = NativeMethods.WmApp + 0x51;
    private const uint ColorKey = 0x00030201;
    private const uint BorderColor = 0x00FF9C33;
    private const uint LabelColor = 0x0024170F;
    private const uint TextColor = 0x00FFFFFF;

    private static readonly NativeMethods.WindowProc WindowProcedure = OverlayWindowProcedure;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<OverlayCommand> _commands = new();
    private readonly Thread _thread;
    private readonly List<nint> _windows = [];
    private readonly string _className = $"RapidPcUseOverlay.{Environment.ProcessId}";
    private Exception? _startupError;
    private uint _threadId;
    private nint _instance;
    private bool _disposed;

    internal ControlOverlay()
    {
        _thread = new Thread(OverlayThreadMain)
        {
            IsBackground = true,
            Name = "Rapid PC Use overlay",
        };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The native control overlay did not initialize within two seconds.");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException("The native control overlay could not initialize.", _startupError);
        }
    }

    internal void Show() => InvokeOnOverlayThread(CreateWindows);

    internal void Hide() => InvokeOnOverlayThread(CloseWindows);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            InvokeOnOverlayThread(CloseWindows);
        }
        catch (Exception exception)
        {
            DriverLog.Warning("overlay.cleanup_failed", "The control cue could not be fully cleaned up.", exception: exception);
        }

        _disposed = true;
        if (_threadId != 0)
        {
            _ = NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, UIntPtr.Zero, nint.Zero);
        }

        _ = _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void OverlayThreadMain()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _instance = NativeMethods.GetModuleHandle(null);
            var windowClass = new NativeMethods.WindowClassEx
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowClassEx>(),
                WindowProcedure = WindowProcedure,
                Instance = _instance,
                Cursor = NativeMethods.LoadCursor(nint.Zero, NativeMethods.IdcArrow),
                ClassName = _className,
            };

            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "RegisterClassEx failed for the control overlay.");
            }

            _ = NativeMethods.PeekMessage(out _, nint.Zero, 0, 0, NativeMethods.PmNoremove);
            _ready.Set();

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "GetMessage failed for the control overlay.");
                }

                if (message.Window == nint.Zero && message.Id == OverlayCommandMessage)
                {
                    DrainCommands();
                    continue;
                }

                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupError ??= exception;
            DriverLog.Error("overlay.thread_failed", "The native control-cue thread failed.", exception);
            _ready.Set();
            FailPendingCommands(exception);
        }
        finally
        {
            CloseWindows();
            if (_instance != nint.Zero)
            {
                _ = NativeMethods.UnregisterClass(_className, _instance);
            }
        }
    }

    private void InvokeOnOverlayThread(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = new OverlayCommand(action);
        _commands.Enqueue(command);
        if (!NativeMethods.PostThreadMessage(_threadId, OverlayCommandMessage, UIntPtr.Zero, nint.Zero))
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Could not dispatch a control-overlay command.");
        }

        if (!command.Completion.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The native control overlay did not respond within two seconds.");
        }

        if (command.Completion.Task.Result is { } exception)
        {
            throw new InvalidOperationException("The native control overlay could not update.", exception);
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            Exception? error = null;
            try
            {
                command.Action();
            }
            catch (Exception exception)
            {
                error = exception;
                DriverLog.Error("overlay.command_failed", "The native control cue could not apply a requested update.", exception);
            }

            command.Completion.TrySetResult(error);
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.TryDequeue(out var command))
        {
            command.Completion.TrySetResult(exception);
        }
    }

    private void CreateWindows()
    {
        CloseWindows();
        try
        {
            foreach (var monitor in MonitorManager.GetMonitors())
            {
                var window = NativeMethods.CreateWindowEx(
                    NativeMethods.WsExLayered | NativeMethods.WsExTransparent | NativeMethods.WsExNoactivate |
                    NativeMethods.WsExToolwindow | NativeMethods.WsExTopmost,
                    _className,
                    string.Empty,
                    NativeMethods.WsPopup,
                    monitor.Left,
                    monitor.Top,
                    monitor.Width,
                    monitor.Height,
                    nint.Zero,
                    nint.Zero,
                    _instance,
                    nint.Zero);

                if (window == nint.Zero)
                {
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "CreateWindowEx failed for the control overlay.");
                }

                _windows.Add(window);
                if (!NativeMethods.SetLayeredWindowAttributes(window, ColorKey, 0, NativeMethods.LwaColorkey))
                {
                    throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Could not configure overlay transparency.");
                }

                _ = NativeMethods.SetWindowDisplayAffinity(window, NativeMethods.WdaExcludefromcapture);
                _ = NativeMethods.SetWindowPos(
                    window,
                    NativeMethods.HwndTopmost,
                    monitor.Left,
                    monitor.Top,
                    monitor.Width,
                    monitor.Height,
                    NativeMethods.SwpNoactivate | NativeMethods.SwpShowwindow);
                _ = NativeMethods.ShowWindow(window, NativeMethods.SwShownoactivate);
                _ = NativeMethods.UpdateWindow(window);
            }
        }
        catch
        {
            CloseWindows();
            throw;
        }
    }

    private void CloseWindows()
    {
        foreach (var window in _windows)
        {
            _ = NativeMethods.DestroyWindow(window);
        }

        _windows.Clear();
    }

    private static nint OverlayWindowProcedure(nint window, uint message, UIntPtr wParam, nint lParam)
    {
        try
        {
            if (message == NativeMethods.WmErasebkgnd)
            {
                return new nint(1);
            }

            if (message == NativeMethods.WmNchittest)
            {
                return new nint(NativeMethods.Httransparent);
            }

            if (message == NativeMethods.WmMouseactivate)
            {
                return new nint(NativeMethods.MaNoactivate);
            }

            if (message == NativeMethods.WmPaint)
            {
                PaintWindow(window);
                return nint.Zero;
            }
        }
        catch (Exception exception)
        {
            DriverLog.Warning("overlay.paint_failed", "The control cue could not repaint correctly.", exception: exception);
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private static void PaintWindow(nint window)
    {
        var dc = NativeMethods.BeginPaint(window, out var paint);
        if (dc == nint.Zero)
        {
            return;
        }

        try
        {
            _ = NativeMethods.GetClientRect(window, out var client);
            using var background = new GdiObject(NativeMethods.CreateSolidBrush(ColorKey));
            _ = NativeMethods.FillRect(dc, ref client, background.Handle);

            using var border = new GdiObject(NativeMethods.CreatePen(NativeMethods.PsSolid, 3, BorderColor));
            var oldPen = NativeMethods.SelectObject(dc, border.Handle);
            var oldBrush = NativeMethods.SelectObject(dc, NativeMethods.GetStockObject(NativeMethods.HollowBrush));
            _ = NativeMethods.Rectangle(dc, 1, 1, Math.Max(2, client.Right - 1), Math.Max(2, client.Bottom - 1));
            _ = NativeMethods.SelectObject(dc, oldBrush);
            _ = NativeMethods.SelectObject(dc, oldPen);

            var label = new NativeMethods.Rect { Left = 0, Top = 0, Right = 292, Bottom = 34 };
            using var labelBrush = new GdiObject(NativeMethods.CreateSolidBrush(LabelColor));
            _ = NativeMethods.FillRect(dc, ref label, labelBrush.Handle);
            var oldFont = NativeMethods.SelectObject(dc, NativeMethods.GetStockObject(NativeMethods.DefaultGuiFont));
            _ = NativeMethods.SetBkMode(dc, NativeMethods.Transparent);
            _ = NativeMethods.SetTextColor(dc, TextColor);
            _ = NativeMethods.DrawText(dc, "AGENT CONTROL  -  ESC TO TAKE OVER", -1, ref label, NativeMethods.DtCenter | NativeMethods.DtSingleline | NativeMethods.DtVcenter);
            _ = NativeMethods.SelectObject(dc, oldFont);
        }
        finally
        {
            _ = NativeMethods.EndPaint(window, ref paint);
        }
    }

    private sealed record OverlayCommand(Action Action)
    {
        internal TaskCompletionSource<Exception?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GdiObject(nint handle) : IDisposable
    {
        internal nint Handle { get; } = handle;

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(Handle);
            }
        }
    }
}
