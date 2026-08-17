using System.Text.Json;

namespace RapidPcUse.Agent;

internal enum PcAgentStatus
{
    Completed,
    NeedsConfirmation,
    Blocked,
    LimitReached,
    Failed,
    Denied,
    UserTakeover,
}

internal enum PcAgentDecisionKind
{
    Act,
    Finish,
    Confirm,
    Blocked,
}

internal enum PcRiskFlag
{
    ExternalCommunication,
    LocalDeletion,
    CredentialEntry,
    PurchaseOrFinancial,
    AccountOrPermissionChange,
    DownloadOrInstall,
    UnclassifiedSensitiveAction,
}

internal sealed record PcRunScope(
    IReadOnlySet<string> AllowedProcesses,
    bool AllowExternalCommunication,
    bool AllowLocalDeletion,
    bool AllowCredentials,
    bool AllowPurchases,
    bool AllowAccountOrPermissionChanges);

internal sealed record PcRunLimits(
    int MaxModelTurns,
    int MaxActions,
    int MaxDurationMilliseconds,
    int MaxConsecutiveNoProgressTurns);

internal sealed record PcRunRequest(
    string Task,
    PcRunScope Scope,
    PcRunLimits Limits,
    bool ReturnFinalScreenshot);

internal sealed record AgentWorkingState(
    string ScreenSummary,
    IReadOnlyList<string> Completed,
    string Next,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Attempted)
{
    internal static AgentWorkingState Empty { get; } = new("", [], "", [], []);
}

internal sealed record AgentActionOutcome(
    IReadOnlyList<string> ActionTypes,
    bool ScreenChanged,
    string ExpectedChange,
    string? ValidationFeedback = null);

internal abstract record PcAgentDecision(PcAgentDecisionKind Kind, AgentWorkingState State);

internal sealed record ActDecision(
    JsonElement Actions,
    AgentWorkingState NextState,
    string ExpectedChange,
    IReadOnlySet<PcRiskFlag> RiskFlags)
    : PcAgentDecision(PcAgentDecisionKind.Act, NextState);

internal sealed record FinishDecision(
    string Summary,
    AgentWorkingState FinalState,
    string VisibleEvidence)
    : PcAgentDecision(PcAgentDecisionKind.Finish, FinalState);

internal sealed record ConfirmDecision(
    string OperationSummary,
    PcRiskFlag Risk,
    AgentWorkingState NextState)
    : PcAgentDecision(PcAgentDecisionKind.Confirm, NextState);

internal sealed record BlockedDecision(
    string Summary,
    string Reason,
    AgentWorkingState FinalState)
    : PcAgentDecision(PcAgentDecisionKind.Blocked, FinalState);

internal sealed record PcModelTurnRequest(
    string Task,
    PcRunScope Scope,
    AgentWorkingState State,
    IReadOnlyList<AgentActionOutcome> RecentOutcomes,
    Observation Observation,
    int Turn,
    int RemainingActions,
    PcRiskFlag? ApprovedRisk,
    string RunId = "");

internal sealed record ProviderTurnTimings(
    long RequestBuildMicroseconds,
    long ResponseHeadersMicroseconds,
    long FirstEventMicroseconds,
    long FirstDecisionDeltaMicroseconds,
    long DecisionCompleteMicroseconds);

internal sealed record ProviderUsage(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens);

internal sealed record PcModelTurnResult(
    PcAgentDecision Decision,
    string Provider,
    string Model,
    int RequestBytes,
    int ImageCount,
    int ImageBytes,
    ProviderTurnTimings Timings,
    ProviderUsage Usage);

internal sealed record PcConfirmation(
    string ConfirmationId,
    string OperationSummary,
    PcRiskFlag Risk,
    DateTimeOffset ExpiresUtc);

internal sealed record PcRunResult(
    PcAgentStatus Status,
    string SessionId,
    string Summary,
    int ModelTurns,
    int ActionsExecuted,
    long ElapsedMilliseconds,
    string TelemetrySessionId,
    PcConfirmation? Confirmation,
    Observation? FinalObservation);

internal interface IPcDesktop
{
    Observation Observe(bool beginControl);

    DesktopActResult Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter);

    CancellationToken ControlCancellationToken { get; }

    void ThrowIfControlLost();

    void Stop();
}

internal interface IPcModelProvider : IDisposable
{
    string Name { get; }

    string Model { get; }

    Task<PcModelTurnResult> DecideAsync(PcModelTurnRequest request, CancellationToken cancellationToken);
}
