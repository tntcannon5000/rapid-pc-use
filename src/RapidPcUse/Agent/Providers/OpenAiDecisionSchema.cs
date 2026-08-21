using System.Text.Json;

namespace RapidPcUse.Agent.Providers;

internal static class OpenAiDecisionSchema
{
    // App-server structured output uses one stable object shape. Irrelevant fields
    // remain empty, which avoids conditional-schema support differences between models.
    internal static void WriteStructuredOutput(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("decision");
        writer.WriteStartObject();
        StringEnum(writer, "act", "finish", "confirm", "handoff", "blocked");
        writer.WriteEndObject();
        writer.WritePropertyName("actions");
        writer.WriteStartObject();
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", SecurityLimits.MaxActionsPerBatch);
        writer.WritePropertyName("items");
        WriteActionUnion(writer);
        writer.WriteEndObject();
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "expected_change", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WritePropertyName("risk_flags");
        writer.WriteStartObject();
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", 8);
        writer.WritePropertyName("items");
        WriteRiskSchema(writer);
        writer.WriteEndObject();
        WriteStringProperty(writer, "completion_guard_text", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "completion_summary", SecurityLimits.MaxAgentSummaryCharacters);
        WriteStringProperty(writer, "summary", SecurityLimits.MaxAgentSummaryCharacters);
        WriteStringProperty(writer, "visible_evidence", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "operation_summary", SecurityLimits.MaxAgentConfirmationSummaryCharacters);
        writer.WritePropertyName("risk");
        writer.WriteStartObject();
        StringEnum(
            writer,
            "none",
            "external_communication",
            "remote_content_change",
            "local_deletion",
            "credential_entry",
            "purchase_or_financial",
            "account_or_permission_change",
            "download_or_install",
            "unclassified_sensitive_action");
        writer.WriteEndObject();
        WriteStringProperty(writer, "reason", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WritePropertyName("handoff_reason");
        writer.WriteStartObject();
        StringEnum(
            writer,
            "none",
            "need_knowledge",
            "need_terminal",
            "need_filesystem",
            "semantic_ambiguity",
            "unsupported_capability");
        writer.WriteEndObject();
        WriteStringProperty(writer, "handoff_request", SecurityLimits.MaxAgentHandoffRequestCharacters);
        writer.WriteEndObject();
        WriteRequired(
            writer,
            "decision",
            "actions",
            "memory",
            "expected_change",
            "risk_flags",
            "completion_guard_text",
            "completion_summary",
            "summary",
            "visible_evidence",
            "operation_summary",
            "risk",
            "reason",
            "handoff_reason",
            "handoff_request");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    internal static void WriteTools(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        WriteFunction(writer, "computer_act", "Execute one short deterministic native Windows action batch, then inspect a fresh screenshot.", WriteActParameters);
        WriteFunction(writer, "computer_finish", "Finish only after the current screenshot visibly confirms the requested result.", WriteFinishParameters);
        WriteFunction(writer, "computer_request_confirmation", "Pause and return to the outer Codex model for explicit user confirmation before a sensitive effect.", WriteConfirmParameters);
        WriteFunction(writer, "computer_handoff", "Pause and ask the outer Codex planner for knowledge, terminal, filesystem, or another capability that visible desktop control cannot provide.", WriteHandoffParameters);
        WriteFunction(writer, "computer_blocked", "Stop because the task cannot safely or reliably continue through the visible desktop.", WriteBlockedParameters);
        writer.WriteEndArray();
    }

    private static void WriteFunction(
        Utf8JsonWriter writer,
        string name,
        string description,
        Action<Utf8JsonWriter> writeParameters)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WriteString("name", name);
        writer.WriteString("description", description);
        writer.WriteBoolean("strict", true);
        writer.WritePropertyName("parameters");
        writeParameters(writer);
        writer.WriteEndObject();
    }

    private static void WriteActParameters(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("actions");
        writer.WriteStartObject();
        writer.WriteString("type", "array");
        writer.WriteNumber("minItems", 1);
        writer.WriteNumber("maxItems", SecurityLimits.MaxActionsPerBatch);
        writer.WritePropertyName("items");
        WriteActionUnion(writer);
        writer.WriteEndObject();
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "expected_change", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WritePropertyName("risk_flags");
        writer.WriteStartObject();
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", 8);
        writer.WritePropertyName("items");
        WriteRiskSchema(writer);
        writer.WriteEndObject();
        WriteStringProperty(writer, "completion_guard_text", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "completion_summary", SecurityLimits.MaxAgentSummaryCharacters);
        writer.WriteEndObject();
        WriteRequired(
            writer,
            "actions",
            "memory",
            "expected_change",
            "risk_flags",
            "completion_guard_text",
            "completion_summary");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteFinishParameters(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        WriteStringProperty(writer, "summary", SecurityLimits.MaxAgentSummaryCharacters);
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "visible_evidence", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WriteEndObject();
        WriteRequired(writer, "summary", "memory", "visible_evidence");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteConfirmParameters(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        WriteStringProperty(writer, "operation_summary", SecurityLimits.MaxAgentConfirmationSummaryCharacters);
        writer.WritePropertyName("risk");
        WriteRiskSchema(writer);
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WriteEndObject();
        WriteRequired(writer, "operation_summary", "risk", "memory");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteBlockedParameters(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        WriteStringProperty(writer, "summary", SecurityLimits.MaxAgentSummaryCharacters);
        WriteStringProperty(writer, "reason", SecurityLimits.MaxAgentStateFieldCharacters);
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WriteEndObject();
        WriteRequired(writer, "summary", "reason", "memory");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteHandoffParameters(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("handoff_reason");
        writer.WriteStartObject();
        StringEnum(
            writer,
            "need_knowledge",
            "need_terminal",
            "need_filesystem",
            "semantic_ambiguity",
            "unsupported_capability");
        writer.WriteEndObject();
        WriteStringProperty(writer, "handoff_request", SecurityLimits.MaxAgentHandoffRequestCharacters, minimumLength: 1);
        WriteStringProperty(writer, "memory", SecurityLimits.MaxAgentStateFieldCharacters);
        writer.WriteEndObject();
        WriteRequired(writer, "handoff_reason", "handoff_request", "memory");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteActionUnion(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("anyOf");
        writer.WriteStartArray();
        WriteAction(writer, "move", [("display_id", "display"), ("x", "coordinate"), ("y", "coordinate")]);
        WriteAction(writer, "click", [("display_id", "display"), ("x", "coordinate"), ("y", "coordinate"), ("button", "button"), ("count", "click_count")]);
        WriteAction(writer, "double_click", [("display_id", "display"), ("x", "coordinate"), ("y", "coordinate"), ("button", "button")]);
        WriteAction(writer, "drag", [("display_id", "display"), ("x", "coordinate"), ("y", "coordinate"), ("to_x", "coordinate"), ("to_y", "coordinate"), ("duration_ms", "drag_duration"), ("button", "button")]);
        WriteAction(writer, "scroll", [("display_id", "display"), ("x", "coordinate"), ("y", "coordinate"), ("scroll_y", "scroll"), ("scroll_x", "scroll")]);
        WriteAction(writer, "type", [("text", "typed_text"), ("interval_ms", "type_interval")]);
        WriteAction(writer, "key", [("keys", "keys")]);
        WriteAction(writer, "wait", [("ms", "wait")]);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAction(Utf8JsonWriter writer, string type, IReadOnlyList<(string Name, string Kind)> fields)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WritePropertyName("enum");
        writer.WriteStartArray();
        writer.WriteStringValue(type);
        writer.WriteEndArray();
        writer.WriteEndObject();
        foreach (var field in fields)
        {
            writer.WritePropertyName(field.Name);
            WriteActionField(writer, field.Kind);
        }

        writer.WriteEndObject();
        WriteRequired(writer, ["type", .. fields.Select(field => field.Name)]);
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
    }

    private static void WriteActionField(Utf8JsonWriter writer, string kind)
    {
        writer.WriteStartObject();
        switch (kind)
        {
            case "display":
                writer.WriteString("type", "string");
                writer.WriteNumber("maxLength", 128);
                break;
            case "coordinate":
                IntegerRange(writer, 0, 1000);
                break;
            case "delta":
                IntegerRange(writer, -32_768, 32_768);
                break;
            case "button":
                StringEnum(writer, "left", "right", "middle", "x1", "x2");
                break;
            case "click_count":
                IntegerRange(writer, 1, 3);
                break;
            case "drag_duration":
                IntegerRange(writer, 0, SecurityLimits.MaxDragMilliseconds);
                break;
            case "scroll":
                IntegerRange(
                    writer,
                    SecurityLimits.MinScrollDeltaPerAction,
                    SecurityLimits.MaxScrollDeltaPerAction);
                break;
            case "typed_text":
                writer.WriteString("type", "string");
                writer.WriteNumber("maxLength", SecurityLimits.MaxTypedCodeUnitsPerAction);
                break;
            case "type_interval":
                IntegerRange(writer, 0, SecurityLimits.MaxTypeIntervalMilliseconds);
                break;
            case "keys":
                writer.WriteString("type", "string");
                writer.WriteNumber("maxLength", SecurityLimits.MaxKeyChordCharacters);
                break;
            case "wait":
                IntegerRange(writer, 0, SecurityLimits.MaxWaitMilliseconds);
                break;
            default:
                throw new InvalidOperationException("Unknown action field schema.");
        }

        writer.WriteEndObject();
    }

    private static void WriteRiskSchema(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        StringEnum(
            writer,
            "external_communication",
            "remote_content_change",
            "local_deletion",
            "credential_entry",
            "purchase_or_financial",
            "account_or_permission_change",
            "download_or_install",
            "unclassified_sensitive_action");
        writer.WriteEndObject();
    }

    private static void WriteStringProperty(
        Utf8JsonWriter writer,
        string name,
        int maximumLength,
        int minimumLength = 0)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        if (minimumLength > 0)
        {
            writer.WriteNumber("minLength", minimumLength);
        }

        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
    }

    private static void IntegerRange(Utf8JsonWriter writer, int minimum, int maximum)
    {
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", minimum);
        writer.WriteNumber("maximum", maximum);
    }

    private static void StringEnum(Utf8JsonWriter writer, params string[] values)
    {
        writer.WriteString("type", "string");
        writer.WritePropertyName("enum");
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteRequired(Utf8JsonWriter writer, params string[] names)
    {
        writer.WritePropertyName("required");
        writer.WriteStartArray();
        foreach (var name in names)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }
}
