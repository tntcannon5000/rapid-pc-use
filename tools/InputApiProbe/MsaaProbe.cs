using System.Runtime.InteropServices;
using Accessibility;

namespace InputApiProbe;

internal static class MsaaProbe
{
    internal static MsaaResult Run(PointSnapshot point, bool invoke)
    {
        try
        {
            var nativePoint = new Native.Point { X = point.X, Y = point.Y };
            var hresult = AccessibleObjectFromPoint(nativePoint, out var accessible, out var child);
            if (hresult < 0 || accessible is null)
            {
                return new MsaaResult(false, hresult, null, null, null, false, null);
            }

            var name = Safe(() => accessible.get_accName(child));
            var role = Safe(() => accessible.get_accRole(child)?.ToString());
            var defaultAction = Safe(() => accessible.get_accDefaultAction(child));
            var invoked = false;
            string? actionError = null;
            if (invoke)
            {
                try
                {
                    accessible.accDoDefaultAction(child);
                    invoked = true;
                }
                catch (Exception exception)
                {
                    actionError = exception.ToString();
                }
            }

            return new MsaaResult(true, hresult, name, role, defaultAction, invoked, actionError);
        }
        catch (Exception exception)
        {
            return new MsaaResult(false, exception.HResult, null, null, null, false, exception.ToString());
        }
    }

    private static string? Safe(Func<string?> value)
    {
        try
        {
            return value();
        }
        catch (Exception exception)
        {
            return $"<{exception.GetType().Name}: 0x{exception.HResult:X8}>";
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        Native.Point point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);
}

internal sealed record MsaaResult(
    bool Accessible,
    int HResult,
    string? Name,
    string? Role,
    string? DefaultAction,
    bool Invoked,
    string? ActionError);
