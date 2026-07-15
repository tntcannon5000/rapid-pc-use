using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RapidPcUse;

internal sealed class PhysicalEscapeHook : IDisposable
{
    private readonly ControlSession _session;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private nint _hook;
    private uint _threadId;
    private bool _swallowEscapeUntilUp;
    private Exception? _startupError;

    internal PhysicalEscapeHook(ControlSession session)
    {
        _session = session;
        _callback = HookCallback;
        _thread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "Rapid PC Use physical Escape hook",
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The physical Escape takeover hook did not initialize within two seconds.");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException("Unable to install the physical Escape takeover hook.", _startupError);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            _ = NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, UIntPtr.Zero, 0);
            _ = _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    private void HookThreadMain()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == 0)
        {
            _startupError = new Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            return;
        }

        _ready.Set();
        try
        {
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
            var isPhysical = (data.Flags & NativeMethods.LlkhfInjected) == 0;
            if (isPhysical && data.VirtualKey == NativeMethods.VkEscape)
            {
                var message = wParam.ToInt32();
                var isDown = message is NativeMethods.WmKeydown or NativeMethods.WmSyskeydown;
                var isUp = message is NativeMethods.WmKeyup or NativeMethods.WmSyskeyup;
                if (isDown && _session.IsActive)
                {
                    _swallowEscapeUntilUp = true;
                    _session.OnPhysicalEscape();
                    return 1;
                }

                if (isUp && _swallowEscapeUntilUp)
                {
                    _swallowEscapeUntilUp = false;
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
