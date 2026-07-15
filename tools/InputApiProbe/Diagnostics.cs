using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace InputApiProbe;

internal static class Diagnostics
{
    internal static EnvironmentSnapshot Capture()
    {
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        var gui = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        Marshal.SetLastPInvokeError(0);
        var guiSuccess = Native.GetGuiThreadInfo(foregroundThread, ref gui);
        var guiError = Marshal.GetLastPInvokeError();

        _ = Native.GetCursorPos(out var logicalCursor);
        _ = Native.GetPhysicalCursorPos(out var physicalCursor);
        _ = Native.GetClipCursor(out var clip);

        var inputDesktop = Native.OpenInputDesktop(
            0,
            false,
            Native.DesktopReadObjects | Native.DesktopWriteObjects);
        var inputDesktopError = inputDesktop == nint.Zero ? Marshal.GetLastPInvokeError() : 0;
        var inputDesktopName = inputDesktop == nint.Zero ? null : UserObjectName(inputDesktop);
        if (inputDesktop != nint.Zero)
        {
            _ = Native.CloseDesktop(inputDesktop);
        }

        return new EnvironmentSnapshot(
            DateTimeOffset.Now,
            Environment.ProcessId,
            Native.GetCurrentThreadId(),
            Process.GetCurrentProcess().SessionId,
            Marshal.SizeOf<Native.Input>(),
            Token(Process.GetCurrentProcess().Handle),
            Point(logicalCursor),
            Point(physicalCursor),
            Rectangle(clip),
            new RectangleSnapshot(
                Native.GetSystemMetrics(Native.SmXVirtualScreen),
                Native.GetSystemMetrics(Native.SmYVirtualScreen),
                Native.GetSystemMetrics(Native.SmXVirtualScreen) + Native.GetSystemMetrics(Native.SmCxVirtualScreen),
                Native.GetSystemMetrics(Native.SmYVirtualScreen) + Native.GetSystemMetrics(Native.SmCyVirtualScreen)),
            UserObjectName(Native.GetProcessWindowStation()),
            UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId())),
            inputDesktopName,
            inputDesktopError,
            Foreground(foreground, foregroundPid, foregroundThread),
            guiSuccess
                ? new GuiSnapshot(
                    Hex(gui.Active),
                    Hex(gui.Focus),
                    Hex(gui.Capture),
                    Hex(gui.MenuOwner),
                    Hex(gui.MoveSize),
                    Hex(gui.Caret))
                : null,
            guiSuccess ? 0 : guiError);
    }

    internal static PointSnapshot PhysicalCursor()
    {
        if (!Native.GetPhysicalCursorPos(out var point))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetPhysicalCursorPos failed.");
        }

        return Point(point);
    }

    internal static string? UserObjectName(nint handle)
    {
        if (handle == nint.Zero)
        {
            return null;
        }

        _ = Native.GetUserObjectInformation(handle, Native.UoiName, nint.Zero, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.GetUserObjectInformation(handle, Native.UoiName, buffer, needed, out _))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ForegroundSnapshot Foreground(nint window, uint pid, uint threadId)
    {
        var title = new StringBuilder(512);
        _ = Native.GetWindowText(window, title, title.Capacity);
        var className = new StringBuilder(256);
        _ = Native.GetClassName(window, className, className.Capacity);
        _ = Native.GetWindowRect(window, out var rectangle);

        TokenSnapshot? token = null;
        int? processSession = null;
        string? processName = null;
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (process != nint.Zero)
        {
            try
            {
                token = Token(process);
                using var managed = Process.GetProcessById((int)pid);
                processSession = managed.SessionId;
                processName = managed.ProcessName;
            }
            catch
            {
                // Snapshot fields remain null when a protected process hides them.
            }
            finally
            {
                _ = Native.CloseHandle(process);
            }
        }

        return new ForegroundSnapshot(
            Hex(window),
            pid,
            threadId,
            processName,
            processSession,
            title.ToString(),
            className.ToString(),
            Rectangle(rectangle),
            token);
    }

    private static TokenSnapshot? Token(nint process)
    {
        if (!Native.OpenProcessToken(process, Native.TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            var integrity = TokenIntegrity(token);
            var elevated = TokenInt(token, Native.TokenElevation);
            var uiAccess = TokenInt(token, Native.TokenUiAccess);
            return new TokenSnapshot(integrity, IntegrityName(integrity), elevated == 1, uiAccess == 1);
        }
        finally
        {
            _ = Native.CloseHandle(token);
        }
    }

    private static int TokenIntegrity(nint token)
    {
        _ = Native.GetTokenInformation(token, Native.TokenIntegrityLevel, nint.Zero, 0, out var needed);
        if (needed == 0)
        {
            return -1;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, needed, out _))
            {
                return -1;
            }

            var label = Marshal.PtrToStructure<Native.TokenMandatoryLabel>(buffer);
            var countPointer = Native.GetSidSubAuthorityCount(label.Label.Sid);
            if (countPointer == nint.Zero)
            {
                return -1;
            }

            var count = Marshal.ReadByte(countPointer);
            var ridPointer = Native.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
            return ridPointer == nint.Zero ? -1 : Marshal.ReadInt32(ridPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int TokenInt(nint token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return Native.GetTokenInformation(token, informationClass, buffer, sizeof(int), out _)
                ? Marshal.ReadInt32(buffer)
                : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string IntegrityName(int rid) => rid switch
    {
        < 0 => "unknown",
        < 0x1000 => "untrusted",
        < 0x2000 => "low",
        < 0x3000 => "medium",
        < 0x4000 => "high",
        < 0x5000 => "system",
        _ => "protected",
    };

    internal static PointSnapshot Point(Native.Point point) => new(point.X, point.Y);

    internal static RectangleSnapshot Rectangle(Native.Rect rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    internal static string Hex(nint value) => $"0x{value.ToInt64():X}";
}

internal sealed record EnvironmentSnapshot(
    DateTimeOffset Timestamp,
    int ProcessId,
    uint ThreadId,
    int SessionId,
    int InputStructSize,
    TokenSnapshot? Token,
    PointSnapshot Cursor,
    PointSnapshot PhysicalCursor,
    RectangleSnapshot Clip,
    RectangleSnapshot VirtualDesktop,
    string? WindowStation,
    string? ThreadDesktop,
    string? InputDesktop,
    int InputDesktopError,
    ForegroundSnapshot Foreground,
    GuiSnapshot? ForegroundGui,
    int ForegroundGuiError);

internal sealed record ForegroundSnapshot(
    string Window,
    uint ProcessId,
    uint ThreadId,
    string? ProcessName,
    int? SessionId,
    string Title,
    string ClassName,
    RectangleSnapshot Rectangle,
    TokenSnapshot? Token);

internal sealed record TokenSnapshot(int IntegrityRid, string Integrity, bool Elevated, bool UiAccess);

internal sealed record GuiSnapshot(string Active, string Focus, string Capture, string MenuOwner, string MoveSize, string Caret);

internal sealed record PointSnapshot(int X, int Y);

internal sealed record RectangleSnapshot(int Left, int Top, int Right, int Bottom)
{
    internal bool Contains(PointSnapshot point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}
