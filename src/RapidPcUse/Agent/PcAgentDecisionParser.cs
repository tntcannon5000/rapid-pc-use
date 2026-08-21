using System.Text;
using System.Text.Json;

namespace RapidPcUse.Agent;

internal static class PcAgentDecisionParser
{
    internal static PcAgentDecision ParseStructured(string output)
    {
        if (output.Length > SecurityLimits.MaxAgentProviderArgumentsCharacters)
        {
            throw new InvalidOperationException("The model decision exceeded the configured argument limit.");
        }

        using var document = JsonDocument.Parse(output, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("decision", out var kind) ||
            kind.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The structured model decision is invalid.");
        }

        return kind.GetString() switch
        {
            "act" => ParseAct(root),
            "finish" => ParseFinish(root),
            "confirm" => ParseConfirm(root),
            "handoff" => ParseHandoff(root),
            "blocked" => ParseBlocked(root),
            _ => throw new InvalidOperationException("The model returned an unknown structured decision."),
        };
    }

    internal static PcAgentDecision Parse(string functionName, string arguments)
    {
        if (arguments.Length > SecurityLimits.MaxAgentProviderArgumentsCharacters)
        {
            throw new InvalidOperationException("The model decision exceeded the configured argument limit.");
        }

        using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The model decision arguments must be an object.");
        }

        return functionName switch
        {
            "computer_act" => ParseAct(root),
            "computer_finish" => ParseFinish(root),
            "computer_request_confirmation" => ParseConfirm(root),
            "computer_handoff" => ParseHandoff(root),
            "computer_blocked" => ParseBlocked(root),
            _ => throw new InvalidOperationException("The model returned an unknown decision tool."),
        };
    }

    internal static AgentWorkingState ParseState(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Agent state must be an object.");
        }

        var state = new AgentWorkingState(
            BoundedString(value, "screen_summary", SecurityLimits.MaxAgentStateFieldCharacters),
            StringList(value, "completed"),
            BoundedString(value, "next", SecurityLimits.MaxAgentStateFieldCharacters),
            StringList(value, "facts"),
            StringList(value, "attempted"));
        ValidateState(state);
        return state;
    }

    internal static void ValidateState(AgentWorkingState state)
    {
        ValidateField(state.ScreenSummary, SecurityLimits.MaxAgentStateFieldCharacters);
        ValidateField(state.Next, SecurityLimits.MaxAgentStateFieldCharacters);
        ValidateEntries(state.Completed);
        ValidateEntries(state.Facts);
        ValidateEntries(state.Attempted);

        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(state));
        if (bytes > SecurityLimits.MaxAgentStateUtf8Bytes)
        {
            throw new InvalidOperationException("Agent state exceeded its bounded memory budget.");
        }
    }

    private static ActDecision ParseAct(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("computer_act requires an actions array.");
        }

        if (actions.GetArrayLength() is < 1 or > SecurityLimits.MaxActionsPerBatch)
        {
            throw new InvalidOperationException("computer_act requires a bounded non-empty actions array.");
        }

        var expectedChange = BoundedString(root, "expected_change", SecurityLimits.MaxAgentStateFieldCharacters);
        var risks = new HashSet<PcRiskFlag>();
        if (root.TryGetProperty("risk_flags", out var riskFlags))
        {
            if (riskFlags.ValueKind != JsonValueKind.Array || riskFlags.GetArrayLength() > 8)
            {
                throw new InvalidOperationException("risk_flags must be a bounded array.");
            }

            foreach (var item in riskFlags.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !TryParseRisk(item.GetString(), out var risk))
                {
                    throw new InvalidOperationException("The model returned an unknown risk flag.");
                }

                risks.Add(risk);
            }
        }

        var completionGuardText = OptionalBoundedString(
            root,
            "completion_guard_text",
            SecurityLimits.MaxAgentStateFieldCharacters);
        var completionSummary = OptionalBoundedString(
            root,
            "completion_summary",
            SecurityLimits.MaxAgentSummaryCharacters);
        if (string.IsNullOrWhiteSpace(completionGuardText) != string.IsNullOrWhiteSpace(completionSummary))
        {
            throw new InvalidOperationException(
                "completion_guard_text and completion_summary must either both be empty or both be populated.");
        }

        return new ActDecision(
            actions.Clone(),
            ParseMemory(root),
            expectedChange,
            risks,
            completionGuardText,
            completionSummary);
    }

    private static FinishDecision ParseFinish(JsonElement root)
    {
        return new FinishDecision(
            BoundedString(root, "summary", SecurityLimits.MaxAgentSummaryCharacters),
            ParseMemory(root),
            BoundedString(root, "visible_evidence", SecurityLimits.MaxAgentStateFieldCharacters));
    }

    private static ConfirmDecision ParseConfirm(JsonElement root)
    {
        var riskText = BoundedString(root, "risk", 64);
        if (!TryParseRisk(riskText, out var risk))
        {
            throw new InvalidOperationException("The model requested confirmation for an unknown risk.");
        }

        return new ConfirmDecision(
            BoundedString(root, "operation_summary", SecurityLimits.MaxAgentConfirmationSummaryCharacters),
            risk,
            ParseMemory(root));
    }

    private static HandoffDecision ParseHandoff(JsonElement root)
    {
        var reasonText = BoundedString(root, "handoff_reason", 64);
        if (!TryParseHandoffReason(reasonText, out var reason))
        {
            throw new InvalidOperationException("The model requested an unknown handoff reason.");
        }

        var request = BoundedString(root, "handoff_request", SecurityLimits.MaxAgentHandoffRequestCharacters);
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new InvalidOperationException("A handoff request must state the bounded assistance needed.");
        }

        return new HandoffDecision(
            reason,
            request,
            ParseMemory(root));
    }

    private static BlockedDecision ParseBlocked(JsonElement root)
        => new(
            BoundedString(root, "summary", SecurityLimits.MaxAgentSummaryCharacters),
            BoundedString(root, "reason", SecurityLimits.MaxAgentStateFieldCharacters),
            ParseMemory(root));

    private static AgentWorkingState ParseMemory(JsonElement root)
        => new(
            BoundedString(root, "memory", SecurityLimits.MaxAgentStateFieldCharacters),
            [],
            "",
            [],
            []);

    private static string BoundedString(JsonElement value, string property, int maximumLength)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"The model decision requires string field '{property}'.");
        }

        var result = element.GetString()!;
        ValidateField(result, maximumLength);
        return result;
    }

    private static string OptionalBoundedString(JsonElement value, string property, int maximumLength)
    {
        if (!value.TryGetProperty(property, out var element))
        {
            return "";
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"The model decision field '{property}' must be a string.");
        }

        var result = element.GetString()!;
        ValidateField(result, maximumLength);
        return result;
    }

    private static List<string> StringList(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Agent state requires array field '{property}'.");
        }

        if (element.GetArrayLength() > SecurityLimits.MaxAgentStateEntries)
        {
            throw new InvalidOperationException("Agent state contains too many entries.");
        }

        var result = new List<string>(element.GetArrayLength());
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Agent state entries must be strings.");
            }

            var text = entry.GetString()!;
            ValidateField(text, SecurityLimits.MaxAgentStateEntryCharacters);
            result.Add(text);
        }

        return result;
    }

    private static void ValidateEntries(IReadOnlyList<string> values)
    {
        if (values.Count > SecurityLimits.MaxAgentStateEntries)
        {
            throw new InvalidOperationException("Agent state contains too many entries.");
        }

        foreach (var value in values)
        {
            ValidateField(value, SecurityLimits.MaxAgentStateEntryCharacters);
        }
    }

    private static void ValidateField(string value, int maximumLength)
    {
        if (value.Length > maximumLength)
        {
            throw new InvalidOperationException("Agent state field exceeded its configured limit.");
        }

        if (value.Contains("base64,", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("data:image", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Agent state may not retain image data.");
        }
    }

    internal static bool TryParseRisk(string? value, out PcRiskFlag risk)
    {
        risk = value switch
        {
            "external_communication" => PcRiskFlag.ExternalCommunication,
            "remote_content_change" => PcRiskFlag.RemoteContentChange,
            "local_deletion" => PcRiskFlag.LocalDeletion,
            "credential_entry" => PcRiskFlag.CredentialEntry,
            "purchase_or_financial" => PcRiskFlag.PurchaseOrFinancial,
            "account_or_permission_change" => PcRiskFlag.AccountOrPermissionChange,
            "download_or_install" => PcRiskFlag.DownloadOrInstall,
            "unclassified_sensitive_action" => PcRiskFlag.UnclassifiedSensitiveAction,
            _ => default,
        };
        return value is "external_communication" or "remote_content_change" or "local_deletion" or "credential_entry" or
            "purchase_or_financial" or "account_or_permission_change" or "download_or_install" or
            "unclassified_sensitive_action";
    }

    internal static string RiskName(PcRiskFlag risk) => risk switch
    {
        PcRiskFlag.ExternalCommunication => "external_communication",
        PcRiskFlag.RemoteContentChange => "remote_content_change",
        PcRiskFlag.LocalDeletion => "local_deletion",
        PcRiskFlag.CredentialEntry => "credential_entry",
        PcRiskFlag.PurchaseOrFinancial => "purchase_or_financial",
        PcRiskFlag.AccountOrPermissionChange => "account_or_permission_change",
        PcRiskFlag.DownloadOrInstall => "download_or_install",
        PcRiskFlag.UnclassifiedSensitiveAction => "unclassified_sensitive_action",
        _ => throw new ArgumentOutOfRangeException(nameof(risk)),
    };

    internal static bool TryParseHandoffReason(string? value, out PcHandoffReason reason)
    {
        reason = value switch
        {
            "need_knowledge" => PcHandoffReason.NeedKnowledge,
            "need_terminal" => PcHandoffReason.NeedTerminal,
            "need_filesystem" => PcHandoffReason.NeedFilesystem,
            "semantic_ambiguity" => PcHandoffReason.SemanticAmbiguity,
            "unsupported_capability" => PcHandoffReason.UnsupportedCapability,
            _ => default,
        };
        return value is "need_knowledge" or "need_terminal" or "need_filesystem" or
            "semantic_ambiguity" or "unsupported_capability";
    }

    internal static string HandoffReasonName(PcHandoffReason reason) => reason switch
    {
        PcHandoffReason.NeedKnowledge => "need_knowledge",
        PcHandoffReason.NeedTerminal => "need_terminal",
        PcHandoffReason.NeedFilesystem => "need_filesystem",
        PcHandoffReason.SemanticAmbiguity => "semantic_ambiguity",
        PcHandoffReason.UnsupportedCapability => "unsupported_capability",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
