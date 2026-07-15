using System.Windows.Automation;

namespace InputApiProbe;

internal static class UiaProbe
{
    internal static UiaResult Inspect(nint window, string? invokeName)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var elements = new List<UiaElementSnapshot>();
            AutomationElement? invokeElement = null;
            var descendants = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
            foreach (AutomationElement element in descendants)
            {
                if (elements.Count >= 500)
                {
                    break;
                }

                try
                {
                    var name = element.Current.Name;
                    var controlType = element.Current.ControlType?.ProgrammaticName;
                    var invoke = element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                    var toggle = element.TryGetCurrentPattern(TogglePattern.Pattern, out _);
                    var selectionItem = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _);
                    var rectangle = element.Current.BoundingRectangle;
                    elements.Add(new UiaElementSnapshot(
                        name,
                        controlType,
                        element.Current.AutomationId,
                        element.Current.ClassName,
                        element.Current.IsEnabled,
                        element.Current.IsOffscreen,
                        invoke,
                        toggle,
                        selectionItem,
                        new RectangleSnapshot(
                            (int)Math.Round(rectangle.Left),
                            (int)Math.Round(rectangle.Top),
                            (int)Math.Round(rectangle.Right),
                            (int)Math.Round(rectangle.Bottom))));

                    if (!string.IsNullOrWhiteSpace(invokeName) &&
                        string.Equals(name, invokeName, StringComparison.OrdinalIgnoreCase))
                    {
                        invokeElement ??= element;
                    }
                }
                catch (Exception)
                {
                    // Elevated or dynamic providers can expose a raw element while denying its properties.
                }
            }

            string? action = null;
            string? actionError = null;
            if (!string.IsNullOrWhiteSpace(invokeName))
            {
                if (invokeElement is null)
                {
                    actionError = $"No UI Automation element named '{invokeName}' was exposed.";
                }
                else
                {
                    try
                    {
                        if (invokeElement.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            action = "invoke";
                        }
                        else if (invokeElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                        {
                            ((SelectionItemPattern)pattern).Select();
                            action = "select";
                        }
                        else if (invokeElement.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                        {
                            ((TogglePattern)pattern).Toggle();
                            action = "toggle";
                        }
                        else
                        {
                            actionError = "The matching element exposed no supported action pattern.";
                        }
                    }
                    catch (Exception exception)
                    {
                        actionError = exception.ToString();
                    }
                }
            }

            return new UiaResult(true, null, elements, action, actionError);
        }
        catch (Exception exception)
        {
            return new UiaResult(false, exception.ToString(), [], null, null);
        }
    }
}

internal sealed record UiaResult(
    bool RootAccessible,
    string? Error,
    IReadOnlyList<UiaElementSnapshot> Elements,
    string? Action,
    string? ActionError);

internal sealed record UiaElementSnapshot(
    string Name,
    string? ControlType,
    string AutomationId,
    string ClassName,
    bool Enabled,
    bool Offscreen,
    bool Invoke,
    bool Toggle,
    bool SelectionItem,
    RectangleSnapshot Rectangle);
