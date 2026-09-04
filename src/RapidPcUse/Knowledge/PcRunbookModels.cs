namespace RapidPcUse.Knowledge;

internal sealed record PcRunbookStep(
    string Id,
    string Kind,
    string Description,
    string ExecutablePath,
    string WorkingDirectory,
    string ExpectedForegroundProcess,
    string ExpectedWindowTitle = "",
    string Effect = "none",
    string HttpMethod = "",
    string HttpUrl = "",
    string HttpBody = "",
    bool RequiresElevation = false,
    bool RequiredBeforeFinish = false,
    IReadOnlyList<string>? Arguments = null,
    int TimeoutMilliseconds = 10_000);

internal sealed record PcRunbook(
    string Key,
    string Title,
    string Summary,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<PcRunbookStep> Steps,
    string Source,
    int Confidence,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision,
    IReadOnlyDictionary<string, PcRunbookStepPerformance>? StepPerformance = null);

internal sealed record PcRunbookStepPerformance(
    int Attempts,
    int Successes,
    int Failures,
    int UncertainEffects,
    long TotalSuccessfulMicroseconds,
    long LastElapsedMicroseconds,
    DateTimeOffset LastAttemptUtc,
    string StepFingerprint = "");

internal enum PcRunbookExecutionDisposition
{
    Success,
    Failure,
    EffectUncertain,
}

internal sealed record PcRunbookExecutionSample(
    PcRunbookStepReference Reference,
    string StepFingerprint,
    PcRunbookExecutionDisposition Disposition,
    long ElapsedMicroseconds);

internal sealed record PcRunbookDocument(
    int Version,
    IReadOnlyList<PcRunbook> Runbooks);

internal sealed record PcRouteRetrievalResult(
    string Context,
    IReadOnlySet<string> RunbookKeys,
    IReadOnlySet<string> ActivatedRunbookKeys,
    IReadOnlyDictionary<PcRunbookStepReference, PcRunbookStep> ExecutableSteps,
    IReadOnlySet<PcRunbookStepReference> RequiredSteps,
    int KnowledgeCount,
    int RunbookCount,
    long ElapsedMicroseconds);

internal sealed record PcRunbookStepReference(
    string RunbookKey,
    string StepId);

internal sealed record PcRunbookExecutionResult(
    string RunbookKey,
    string StepId,
    string Description,
    string ForegroundProcess,
    bool TargetObserved,
    long DispatchMicroseconds,
    long ReadinessMicroseconds,
    long TotalMicroseconds,
    bool ReusedExistingTarget = false,
    string ResultContext = "",
    string StepKind = "",
    bool EffectUncertain = false);
