using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RapidPcUse.Agent;

internal interface IForegroundWindowInspector
{
    string GetProcessName();
}

internal sealed class ForegroundWindowInspector : IForegroundWindowInspector
{
    public string GetProcessName()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            throw new InvalidOperationException("Windows did not report a foreground application.");
        }

        _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using var process = Process.GetProcessById(checked((int)processId));
        return process.ProcessName;
    }
}
