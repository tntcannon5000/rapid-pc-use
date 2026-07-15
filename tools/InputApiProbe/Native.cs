using System.Runtime.InteropServices;
using System.Text;

namespace InputApiProbe;

internal static class Native
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const int TokenIntegrityLevel = 25;
    internal const int TokenUiAccess = 26;
    internal const int UoiFlags = 1;
    internal const int UoiName = 2;
    internal const uint DesktopReadObjects = 0x0001;
    internal const uint DesktopWriteObjects = 0x0080;
    internal const uint MouseEventMove = 0x0001;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventAbsolute = 0x8000;
    internal const uint MouseEventVirtualDesk = 0x4000;
    internal const uint InputMouse = 0;
    internal const uint PointerInputTypeTouch = 2;
    internal const uint PointerFlagInRange = 0x00000002;
    internal const uint PointerFlagInContact = 0x00000004;
    internal const uint PointerFlagPrimary = 0x00002000;
    internal const uint PointerFlagDown = 0x00010000;
    internal const uint PointerFlagUp = 0x00040000;
    internal const uint TouchMaskContactArea = 0x00000001;
    internal const uint TouchMaskOrientation = 0x00000002;
    internal const uint TouchMaskPressure = 0x00000004;
    internal const uint TouchFeedbackNone = 3;
    internal const uint ButtonClick = 0x00F5;
    internal const uint SendMessageAbortIfHung = 0x0002;
    internal const byte VirtualKeyMenu = 0x12;
    internal const byte VirtualKeyTab = 0x09;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    internal static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClipCursor(out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClipCursor(nint rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClipCursor(ref Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeTouchInjection(uint maximumCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(nint window, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint window, StringBuilder className, int maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    internal static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    internal static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(nint handle, int index, nint information, uint length, out uint needed);

    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGuiThreadInfo(uint threadId, ref GuiThreadInfo information);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(nint token, int informationClass, nint information, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern nint GetSidSubAuthority(nint sid, uint index);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal nint Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenMandatoryLabel
    {
        internal SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Active;
        internal nint Focus;
        internal nint Capture;
        internal nint MenuOwner;
        internal nint MoveSize;
        internal nint Caret;
        internal Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerInfo
    {
        internal uint PointerType;
        internal uint PointerId;
        internal uint FrameId;
        internal uint PointerFlags;
        internal nint SourceDevice;
        internal nint TargetWindow;
        internal Point PixelLocation;
        internal Point HimetricLocation;
        internal Point PixelLocationRaw;
        internal Point HimetricLocationRaw;
        internal uint Time;
        internal uint HistoryCount;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerTouchInfo
    {
        internal PointerInfo PointerInfo;
        internal uint TouchFlags;
        internal uint TouchMask;
        internal Rect Contact;
        internal Rect ContactRaw;
        internal uint Orientation;
        internal uint Pressure;
    }

}
