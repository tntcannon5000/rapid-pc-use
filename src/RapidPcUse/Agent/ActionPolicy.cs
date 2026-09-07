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

internal static class ActionPolicy
{
    internal static ActionPolicyResult Evaluate(ActDecision decision, PcRunScope scope, PcRiskFlag? approvedRisk)
    {
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

    private static bool RequiresConfirmation(PcRiskFlag risk, PcRunScope scope) => risk switch
    {
        PcRiskFlag.LocalProcessLaunch => !scope.AllowLocalProcessLaunches,
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
        PcRiskFlag.LocalProcessLaunch => 5,
        PcRiskFlag.ExternalCommunication => 6,
        PcRiskFlag.RemoteContentChange => 7,
        PcRiskFlag.LocalDeletion => 8,
        _ => 9,
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
