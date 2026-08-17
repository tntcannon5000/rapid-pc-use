using System.Runtime.InteropServices;

namespace RapidPcUse;

internal static class NativeMethods
{
    internal const int MonitorInfoPrimary = 0x00000001;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;

    internal const uint Srccopy = 0x00CC0020;
    internal const uint CaptureBlt = 0x40000000;
    internal const uint DiNormal = 0x0003;
    internal const int CursorShowing = 0x00000001;

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint MouseeventfMove = 0x0001;
    internal const uint MouseeventfLeftdown = 0x0002;
    internal const uint MouseeventfLeftup = 0x0004;
    internal const uint MouseeventfRightdown = 0x0008;
    internal const uint MouseeventfRightup = 0x0010;
    internal const uint MouseeventfMiddledown = 0x0020;
    internal const uint MouseeventfMiddleup = 0x0040;
    internal const uint MouseeventfXdown = 0x0080;
    internal const uint MouseeventfXup = 0x0100;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfHwheel = 0x1000;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint KeyeventfUnicode = 0x0004;
    internal const int WheelDelta = 120;
    internal const uint Xbutton1 = 0x0001;
    internal const uint Xbutton2 = 0x0002;

    internal const int WhKeyboardLl = 13;
    internal const int WmKeydown = 0x0100;
    internal const int WmKeyup = 0x0101;
    internal const int WmSyskeydown = 0x0104;
    internal const int WmSyskeyup = 0x0105;
    internal const int WmPaint = 0x000F;
    internal const int WmErasebkgnd = 0x0014;
    internal const int WmNchittest = 0x0084;
    internal const int WmMouseactivate = 0x0021;
    internal const int WmQuit = 0x0012;
    internal const uint WmApp = 0x8000;
    internal const uint LlkhfInjected = 0x00000010;
    internal const int VkEscape = 0x1B;

    internal const uint WsPopup = 0x80000000;
    internal const uint WsExTopmost = 0x00000008;
    internal const uint WsExTransparent = 0x00000020;
    internal const uint WsExToolwindow = 0x00000080;
    internal const uint WsExLayered = 0x00080000;
    internal const uint WsExNoactivate = 0x08000000;
    internal const uint SwpNoactivate = 0x0010;
    internal const uint SwpShowwindow = 0x0040;
    internal const uint WdaExcludefromcapture = 0x00000011;
    internal const uint LwaColorkey = 0x00000001;
    internal const int SwShownoactivate = 4;
    internal const uint PmNoremove = 0x0000;
    internal const int PsSolid = 0;
    internal const int HollowBrush = 5;
    internal const int DefaultGuiFont = 17;
    internal const int Transparent = 1;
    internal const int Httransparent = -1;
    internal const int MaNoactivate = 3;
    internal const uint DtCenter = 0x00000001;
    internal const uint DtVcenter = 0x00000004;
    internal const uint DtSingleline = 0x00000020;
    internal static readonly nint IdcArrow = new(32512);
    internal static readonly nint HwndTopmost = new(-1);
    internal static readonly UIntPtr InputSentinel = new(0x52504355);

    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);
    internal delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    internal delegate nint WindowProc(nint window, uint message, UIntPtr wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorInfo
    {
        internal int Size;
        internal int Flags;
        internal nint Cursor;
        internal Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool IsIcon;
        internal uint HotspotX;
        internal uint HotspotY;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
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
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookData
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal UIntPtr WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal WindowProc WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        internal nint Dc;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Erase;

        internal Rect Paint;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Restore;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool IncrementalUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Reserved;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, uint operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CursorInfo cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, int step, nint brush, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScan(char character);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, nint lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint window, uint message, UIntPtr wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint BeginPaint(nint window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(nint window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern int FillRect(nint dc, ref Rect rect, nint brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawText(nint dc, string text, int count, ref Rect rect, uint format);

    [DllImport("user32.dll")]
    internal static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern nint CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    internal static extern nint GetStockObject(int index);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Rectangle(nint dc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(nint dc, uint color);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    internal static void TryEnablePerMonitorV2DpiAwareness()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new nint(-4));
        }
        catch (EntryPointNotFoundException)
        {
            // The manifest still provides DPI awareness on older Windows builds.
        }
    }
}
