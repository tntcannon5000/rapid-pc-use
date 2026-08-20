using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace RapidPcUse.Agent;

internal sealed record CompletionGuardResult(
    bool Matched,
    int ElementsInspected,
    long ElapsedMicroseconds,
    string Outcome);

internal interface ICompletionGuardVerifier
{
    CompletionGuardResult Verify(string expectedText);
}

internal sealed class UiaCompletionGuardVerifier : ICompletionGuardVerifier
{
    private const int MaximumElements = 512;
    private const int MaximumWindowTextCharacters = 4096;

    public CompletionGuardResult Verify(string expectedText)
    {
        var started = Stopwatch.GetTimestamp();
        var inspected = 0;
        try
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == nint.Zero)
            {
                return Result(false, inspected, started, "no_foreground_window");
            }

            if (MatchesNativeWindowText(window, expectedText, ref inspected))
            {
                return Result(true, inspected, started, "matched_native");
            }

            var root = AutomationElement.FromHandle(window);
            if (Matches(root, expectedText))
            {
                return Result(true, inspected + 1, started, "matched_uia");
            }

            AutomationElementCollection descendants;
            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.TreeScope = TreeScope.Element;
            using (cache.Activate())
            {
                descendants = root.FindAll(TreeScope.Descendants, Automation.ControlViewCondition);
            }

            foreach (AutomationElement element in descendants)
            {
                if (inspected >= MaximumElements)
                {
                    return Result(false, inspected, started, "element_limit");
                }

                inspected++;
                try
                {
                    if (!element.Cached.IsOffscreen &&
                        element.Cached.Name.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result(true, inspected, started, "matched_uia");
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Dynamic UI trees can invalidate individual elements while enumerating.
                }
            }

            return Result(false, inspected, started, "not_found");
        }
        catch (ElementNotAvailableException)
        {
            return Result(false, inspected, started, "root_unavailable");
        }
        catch (InvalidOperationException)
        {
            return Result(false, inspected, started, "provider_unavailable");
        }
        catch (COMException)
        {
            return Result(false, inspected, started, "provider_error");
        }
        catch (UnauthorizedAccessException)
        {
            return Result(false, inspected, started, "access_denied");
        }
    }

    private static bool Matches(AutomationElement element, string expectedText)
        => element.Current.Name.Contains(expectedText, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesNativeWindowText(nint root, string expectedText, ref int inspected)
    {
        var matched = WindowTextContains(root, expectedText);
        inspected++;
        if (matched)
        {
            return true;
        }

        var localInspected = inspected;
        NativeMethods.WindowEnumProc callback = (window, _) =>
        {
            if (localInspected >= MaximumElements)
            {
                return false;
            }

            localInspected++;
            matched = WindowTextContains(window, expectedText);
            return !matched;
        };
        _ = NativeMethods.EnumChildWindows(root, callback, nint.Zero);
        GC.KeepAlive(callback);
        inspected = localInspected;
        return matched;
    }

    private static bool WindowTextContains(nint window, string expectedText)
    {
        if (!NativeMethods.IsWindowVisible(window))
        {
            return false;
        }

        var length = NativeMethods.GetWindowTextLength(window);
        if (length is <= 0 or > MaximumWindowTextCharacters)
        {
            return false;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return copied > 0 &&
            new string(buffer, 0, copied).Contains(expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static CompletionGuardResult Result(bool matched, int inspected, long started, string outcome)
        => new(
            matched,
            inspected,
            (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000),
            outcome);
}
