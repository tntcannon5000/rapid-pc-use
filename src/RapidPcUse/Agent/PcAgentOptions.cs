using System.Text.RegularExpressions;

namespace RapidPcUse.Agent;

internal sealed record PcAgentOptions(
    bool Enabled,
    string Provider,
    string Model,
    string ReasoningEffort,
    string ServiceTier,
    int MaxModelTurns,
    int MaxActions,
    int MaxDurationMilliseconds,
    int MaxConsecutiveNoProgressTurns,
    string ImageDetail)
{
    private static readonly Regex SafeIdentifier = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static PcAgentOptions FromEnvironment()
    {
        var provider = ReadIdentifier("RAPID_PC_AGENT_PROVIDER", "codex").ToLowerInvariant();
        var model = ReadIdentifier("RAPID_PC_AGENT_MODEL", "gpt-5.6-sol");
        var reasoning = ReadEnum("RAPID_PC_AGENT_REASONING", "medium", "none", "low", "medium", "high");
        var serviceTier = ReadEnum("RAPID_PC_AGENT_SERVICE_TIER", "fast", "fast", "flex");
        var imageDetail = ReadEnum("RAPID_PC_AGENT_IMAGE_DETAIL", "original", "auto", "low", "high", "original");
        var hasOpenAiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var enabled = ReadOptionalBoolean("RAPID_PC_AGENT_ENABLED") ??
            (provider is "codex" or "broker" || provider == "openai" && hasOpenAiKey);

        return new PcAgentOptions(
            enabled,
            provider,
            model,
            reasoning,
            serviceTier,
            ReadInteger("RAPID_PC_AGENT_MAX_TURNS", 48, 1, SecurityLimits.MaxAgentModelTurns),
            ReadInteger("RAPID_PC_AGENT_MAX_ACTIONS", 96, 1, SecurityLimits.MaxAgentActions),
            ReadInteger("RAPID_PC_AGENT_MAX_DURATION_MS", 120_000, 10_000, SecurityLimits.MaxAgentDurationMilliseconds),
            ReadInteger("RAPID_PC_AGENT_NO_PROGRESS_LIMIT", 3, 1, SecurityLimits.MaxAgentNoProgressTurns),
            imageDetail);
    }

    private static string ReadIdentifier(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim() ?? fallback;
        if (!SafeIdentifier.IsMatch(value))
        {
            throw new InvalidOperationException($"{name} is invalid.");
        }

        return value;
    }

    private static string ReadEnum(string name, string fallback, params string[] allowed)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() ?? fallback;
        if (!allowed.Contains(value))
        {
            throw new InvalidOperationException($"{name} is invalid.");
        }

        return value;
    }

    private static int ReadInteger(string name, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{name} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    private static bool? ReadOptionalBoolean(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant();
        return raw switch
        {
            null or "" => null,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidOperationException($"{name} must be 0, 1, true, or false."),
        };
    }
}
