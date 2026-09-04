namespace RapidPcUse;

internal static class SecurityLimits
{
    internal const int MaxRequestLineCharacters = 1_048_576;
    internal const int MaxActionsPerBatch = 32;
    internal const int MaxTypedCodeUnitsPerAction = 10_000;
    internal const int MaxKeyChordCharacters = 128;
    internal const int MaxWaitMilliseconds = 10_000;
    internal const int MaxDragMilliseconds = 10_000;
    internal const int MaxTypeIntervalMilliseconds = 25;
    // Computer-use models express scrolling as a screen-space delta. The
    // executor converts each 100 delta units to one Windows wheel notch.
    internal const int ScrollDeltaPerWheelTick = 100;
    internal const int MinScrollDeltaPerAction = -10_000;
    internal const int MaxScrollDeltaPerAction = 10_000;
    internal const int MaxWheelTicksPerAction = 100;
    internal const int MaxBatchMilliseconds = 30_000;
    internal const int MaxDisplays = 8;
    internal const long MaxPixelsPerDisplay = 40_000_000;
    internal const long MaxTotalCapturePixels = 100_000_000;
    internal const int MaxAgentTaskCharacters = 4_000;
    internal const int MaxAgentSummaryCharacters = 1_000;
    internal const int MaxAgentStateUtf8Bytes = 2_048;
    internal const int MaxAgentStateFieldCharacters = 400;
    internal const int MaxAgentStateEntries = 8;
    internal const int MaxAgentStateEntryCharacters = 160;
    internal const int MaxAgentRecentOutcomes = 3;
    internal const int MaxAgentAllowedProcesses = 16;
    internal const int MaxAgentProcessNameCharacters = 64;
    internal const int MaxAgentModelTurns = 50;
    internal const int MaxAgentActions = 256;
    internal const int MaxAgentDurationMilliseconds = 300_000;
    internal const int MaxAgentNoProgressTurns = 8;
    internal const int MaxAgentProviderResponseBytes = 2_000_000;
    internal const int MaxAgentProviderEventBytes = 1_000_000;
    internal const int MaxAgentProviderArgumentsCharacters = 200_000;
    internal const int MaxAgentProviderErrorBytes = 16_384;
    internal const int MaxAgentOutputTokens = 1_024;
    internal const int MaxAgentConfirmationSummaryCharacters = 600;
    internal const int MaxAgentHandoffRequestCharacters = 600;
    internal const int MaxAgentOuterContextCharacters = 1_200;
    internal const int MaxAgentRetrievalQueryCharacters = 240;
    internal const int MaxAgentRetrievedContextCharacters = 2_400;
    internal const int MaxAgentRunbookKeyCharacters = 96;
    internal const int MaxAgentRunbookStepIdCharacters = 64;
    internal const int MaxAgentLaunchUriCharacters = 2_048;
    internal static readonly TimeSpan MaxFrameAge = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan AgentConfirmationLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AgentHandoffLifetime = TimeSpan.FromMinutes(5);
}
