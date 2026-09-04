using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using RapidPcUse.Agent;

namespace RapidPcUse;

internal sealed class PcLaunchCoordinator : IPcLaunchCoordinator
{
    private const int MaximumReadinessWaitMilliseconds = 1_200;
    private const int ReadinessPollMilliseconds = 25;
    private static readonly HashSet<string> AllowedWebSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
    };
    private static readonly string[] BrowserProcessNames =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
    ];

    public PcLaunchTiming Launch(string launchUri, Action checkOperation)
    {
        var uri = ValidateUri(launchUri);
        checkOperation();
        var foregroundBeforeDispatch = NativeMethods.GetForegroundWindow();
        var existingTargetWindows = TargetWindows(uri)
            .Select(window => window.Handle)
            .ToHashSet();
        var handlerProcessName = HandlerProcessName(uri);
        var totalStarted = Stopwatch.GetTimestamp();
        var dispatchStarted = Stopwatch.GetTimestamp();
        using (Process.Start(new ProcessStartInfo(uri.OriginalString) { UseShellExecute = true }))
        {
        }

        var dispatchCompleted = Stopwatch.GetTimestamp();
        var readinessStarted = dispatchCompleted;
        var targetFound = false;
        var targetActivated = false;
        while (Stopwatch.GetElapsedTime(readinessStarted).TotalMilliseconds < MaximumReadinessWaitMilliseconds)
        {
            checkOperation();
            var targetWindows = TargetWindows(uri);
            var target = SelectLaunchTarget(
                targetWindows,
                existingTargetWindows,
                foregroundBeforeDispatch,
                NativeMethods.GetForegroundWindow(),
                handlerProcessName);

            if (target != IntPtr.Zero && Stopwatch.GetElapsedTime(readinessStarted).TotalMilliseconds >= 100)
            {
                targetFound = true;
                _ = TryActivateWindow(target);
                if (NativeMethods.GetForegroundWindow() == target)
                {
                    targetActivated = true;
                    break;
                }
            }

            Thread.Sleep(ReadinessPollMilliseconds);
        }

        checkOperation();
        var completed = Stopwatch.GetTimestamp();
        return new PcLaunchTiming(
            ElapsedMicroseconds(dispatchStarted, dispatchCompleted),
            ElapsedMicroseconds(readinessStarted, completed),
            ElapsedMicroseconds(totalStarted, completed),
            uri.Scheme.ToLowerInvariant(),
            targetFound,
            targetActivated,
            ForegroundProcessName());
    }

    private static List<PcLaunchWindow> TargetWindows(Uri uri)
    {
        var windows = new List<PcLaunchWindow>();
        foreach (var processName in TargetProcessNames(uri))
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var window = process.MainWindowHandle;
                        if (window != IntPtr.Zero && NativeMethods.IsWindowVisible(window))
                        {
                            windows.Add(new PcLaunchWindow(window, process.ProcessName));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Multi-process applications may exit while enumerated.
                    }
                }
            }
        }

        return windows;
    }

    internal static nint SelectLaunchTarget(
        IReadOnlyList<PcLaunchWindow> currentWindows,
        IReadOnlySet<nint> existingWindows,
        nint foregroundBeforeDispatch,
        nint foregroundNow,
        string handlerProcessName)
    {
        var newWindow = currentWindows.FirstOrDefault(window =>
            !existingWindows.Contains(window.Handle) &&
            (handlerProcessName.Length == 0 ||
             string.Equals(window.ProcessName, handlerProcessName, StringComparison.OrdinalIgnoreCase)));
        if (newWindow is not null)
        {
            return newWindow.Handle;
        }

        var foreground = currentWindows.FirstOrDefault(window => window.Handle == foregroundNow);
        if (foreground is null)
        {
            return IntPtr.Zero;
        }

        if (foregroundNow != foregroundBeforeDispatch &&
            (handlerProcessName.Length == 0 ||
             string.Equals(foreground.ProcessName, handlerProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return foreground.Handle;
        }

        return handlerProcessName.Length > 0 &&
               string.Equals(foreground.ProcessName, handlerProcessName, StringComparison.OrdinalIgnoreCase)
            ? foreground.Handle
            : IntPtr.Zero;
    }

    private static string HandlerProcessName(Uri uri)
    {
        if (!AllowedWebSchemes.Contains(uri.Scheme))
        {
            return "Discord";
        }

        try
        {
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{uri.Scheme}\UserChoice");
            var programId = userChoice?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(programId))
            {
                return "";
            }

            using var commandKey = Registry.ClassesRoot.OpenSubKey($@"{programId}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            var executable = ExecutableFromCommand(command);
            var processName = Path.GetFileNameWithoutExtension(executable);
            return BrowserProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
                ? processName
                : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }

    private static string ExecutableFromCommand(string? command)
    {
        command = command?.TrimStart();
        if (string.IsNullOrEmpty(command))
        {
            return "";
        }

        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : "";
        }

        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0 ? command : command[..firstSpace];
    }

    internal static string ForegroundProcessName()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0)
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "unknown";
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string[] TargetProcessNames(Uri uri)
        => AllowedWebSchemes.Contains(uri.Scheme) ? BrowserProcessNames : ["Discord"];

    internal static bool TryActivateWindow(nint target)
    {
        if (NativeMethods.IsIconic(target))
        {
            _ = NativeMethods.ShowWindow(target, NativeMethods.SwRestore);
        }

        if (NativeMethods.GetForegroundWindow() == target)
        {
            return true;
        }

        _ = NativeMethods.SetForegroundWindow(target);
        if (NativeMethods.GetForegroundWindow() == target)
        {
            return true;
        }

        // Windows normally prevents a background process from stealing focus. The driver already
        // owns the visible desktop control lease here, so temporarily join the relevant input
        // queues and perform the same activation explicitly. No synthetic keystroke is injected.
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
        var attachedForeground = AttachInputThread(currentThread, foregroundThread);
        var attachedTarget = AttachInputThread(currentThread, targetThread);
        try
        {
            _ = NativeMethods.BringWindowToTop(target);
            _ = NativeMethods.SetActiveWindow(target);
            _ = NativeMethods.SetForegroundWindow(target);
            if (NativeMethods.GetForegroundWindow() != target)
            {
                NativeMethods.SwitchToThisWindow(target, true);
            }

            return NativeMethods.GetForegroundWindow() == target;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static bool AttachInputThread(uint currentThread, uint otherThread)
        => otherThread != 0 && otherThread != currentThread &&
           NativeMethods.AttachThreadInput(currentThread, otherThread, true);

    internal static Uri ValidateUri(string launchUri)
    {
        if (string.IsNullOrWhiteSpace(launchUri) ||
            launchUri.Length > SecurityLimits.MaxAgentLaunchUriCharacters ||
            launchUri.Any(char.IsControl) ||
            !Uri.TryCreate(launchUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("launch_uri must be a bounded absolute HTTP(S) URL or the exact discord: application URI.");
        }

        if (AllowedWebSchemes.Contains(uri.Scheme))
        {
            if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("launch_uri web URLs require a host and cannot contain embedded credentials.");
            }

            return uri;
        }

        if (string.Equals(uri.Scheme, "discord", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(launchUri, "discord:", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        throw new ArgumentException("launch_uri uses a scheme that Rapid PC Use does not launch directly.");
    }

    private static long ElapsedMicroseconds(long started, long completed)
        => (long)(Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds * 1_000);

    internal sealed record PcLaunchWindow(nint Handle, string ProcessName);
}
