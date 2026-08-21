using System.Text.Json;

namespace RapidPcUse.Agent;

internal sealed record ActionPolicyResult(
    bool Allowed,
    PcRiskFlag? ConfirmationRisk,
    string? BlockReason,
    bool ConsumedApprovedRisk);

internal interface IForegroundWindowInspector
{
    string GetProcessName();
}

internal sealed class ActionPolicy(IForegroundWindowInspector windowInspector)
{
    internal ActionPolicyResult Evaluate(ActDecision decision, PcRunScope scope, PcRiskFlag? approvedRisk)
    {
        if (scope.AllowedProcesses.Count > 0)
        {
            var processName = NormalizeProcessName(windowInspector.GetProcessName());
            if (!scope.AllowedProcesses.Contains(processName))
            {
                return new ActionPolicyResult(false, null, "The foreground application is outside the authorized process scope.", false);
            }
        }

        var risks = new HashSet<PcRiskFlag>(decision.RiskFlags);
        AddInferredRisks(decision.Actions, risks);
        var consumedApproval = false;
        foreach (var risk in risks.OrderBy(RiskOrder))
        {
            if (approvedRisk == risk)
            {
                consumedApproval = true;
                continue;
            }

            if (RequiresConfirmation(risk, scope))
            {
                return new ActionPolicyResult(false, risk, null, false);
            }
        }

        return new ActionPolicyResult(true, null, null, consumedApproval);
    }

    internal static string NormalizeProcessName(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        if (normalized.Length == 0 || normalized.Length > SecurityLimits.MaxAgentProcessNameCharacters ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new InvalidOperationException("The foreground process name is invalid.");
        }

        return normalized.ToLowerInvariant();
    }

    private static bool RequiresConfirmation(PcRiskFlag risk, PcRunScope scope) => risk switch
    {
        PcRiskFlag.ExternalCommunication => !scope.AllowExternalCommunication,
        PcRiskFlag.RemoteContentChange => !scope.AllowRemoteContentChanges,
        PcRiskFlag.LocalDeletion => !scope.AllowLocalDeletion,
        PcRiskFlag.CredentialEntry => true,
        PcRiskFlag.PurchaseOrFinancial => true,
        PcRiskFlag.AccountOrPermissionChange => true,
        PcRiskFlag.DownloadOrInstall => true,
        PcRiskFlag.UnclassifiedSensitiveAction => true,
        _ => true,
    };

    private static int RiskOrder(PcRiskFlag risk) => risk switch
    {
        PcRiskFlag.PurchaseOrFinancial => 0,
        PcRiskFlag.CredentialEntry => 1,
        PcRiskFlag.AccountOrPermissionChange => 2,
        PcRiskFlag.DownloadOrInstall => 3,
        PcRiskFlag.UnclassifiedSensitiveAction => 4,
        PcRiskFlag.ExternalCommunication => 5,
        PcRiskFlag.RemoteContentChange => 6,
        PcRiskFlag.LocalDeletion => 7,
        _ => 8,
    };

    private static void AddInferredRisks(JsonElement actions, HashSet<PcRiskFlag> risks)
    {
        foreach (var action in actions.EnumerateArray())
        {
            if (!action.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var actionType = type.GetString();
            if (actionType is not ("key" or "key_down" or "key_up") ||
                !action.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var chord = keys.GetString()!;
            if (chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(key => key.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || key.Equals("DEL", StringComparison.OrdinalIgnoreCase)))
            {
                // Model-declared risk flags may add confirmation requirements,
                // but can never suppress the driver's conservative inference.
                risks.Add(PcRiskFlag.LocalDeletion);
            }
        }
    }
}
