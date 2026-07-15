using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RapidPcUse;

internal static class MonitorManager
{
    internal static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        var index = 0;
        NativeMethods.MonitorEnumProc callback = (nint monitor, nint hdc, ref NativeMethods.Rect monitorRect, nint data) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
            }

            var rect = info.Monitor;
            monitors.Add(new MonitorDescriptor(
                $"display-{index++}",
                info.DeviceName,
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                (info.Flags & NativeMethods.MonitorInfoPrimary) != 0));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(0, 0, callback, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }

        return monitors.OrderByDescending(monitor => monitor.IsPrimary).ThenBy(monitor => monitor.Id).ToArray();
    }

    internal static string GetTopologyKey(IReadOnlyList<MonitorDescriptor> monitors)
    {
        var builder = new StringBuilder();
        foreach (var monitor in monitors)
        {
            _ = builder.Append(monitor.DeviceName)
                .Append(':').Append(monitor.Left)
                .Append(',').Append(monitor.Top)
                .Append(',').Append(monitor.Width)
                .Append('x').Append(monitor.Height)
                .Append(';');
        }

        return builder.ToString();
    }

    internal static MonitorDescriptor Find(IReadOnlyList<MonitorDescriptor> monitors, string id)
    {
        return monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown display_id '{id}'. Observe again to get the current displays.");
    }
}
