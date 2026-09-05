using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RapidPcUse.Agent.Providers;
using RapidPcUse.Knowledge;

namespace RapidPcUse.Agent;

internal sealed partial class PcAgentLoop : IDisposable
{
    private readonly object _gate = new();
    private readonly IPcDesktop _desktop;
    private readonly IPcModelProvider _provider;
    private readonly PcAgentOptions _options;
    private readonly ActionPolicy _policy;
    private readonly ICompletionGuardVerifier _completionGuard;
    private readonly IPcLaunchCoordinator _launcher;
    private readonly IPcLocalRouteRuntime _localRoutes;
    private readonly TimeProvider _timeProvider;
    private readonly VisualMemory _visualMemory = new();
    private RunSession? _paused;
    private bool _active;
    private bool _disposed;

    internal PcAgentLoop(
        IPcDesktop desktop,
        IPcModelProvider provider,
        PcAgentOptions options,
        IForegroundWindowInspector? windowInspector = null,
        TimeProvider? timeProvider = null,
        ICompletionGuardVerifier? completionGuard = null,
        IPcLaunchCoordinator? launcher = null,
        IPcLocalRouteRuntime? localRoutes = null)
    {
        _desktop = desktop;
        _provider = provider;
        _options = options;
        _policy = new ActionPolicy(windowInspector ?? new ForegroundWindowInspector());
        _completionGuard = completionGuard ?? new UiaCompletionGuardVerifier();
        _launcher = launcher ?? new PcLaunchCoordinator();
        _localRoutes = localRoutes ?? new PcLocalRouteRuntime();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal PcRunResult Run(PcRunRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRequest(request);
        lock (_gate)
        {
            if (_active || _paused is not null)
            {
                throw new InvalidOperationException("A Rapid PC Use agent run is already active or paused.");
            }

            _active = true;
        }

        var session = new RunSession(request);
        AgentTelemetry.RunStarted(session.RunId, _provider, request);
        try
        {
            return ContinueAsync(session).GetAwaiter().GetResult();
        }
        finally
        {
            lock (_gate)
            {
                _active = false;
            }
        }
    }

    internal PcAgentOptions Options => _options;

    internal int RetainedImageCount => _visualMemory.RetainedImageCount;

    internal PcRunResult Resume(string sessionId, string confirmationId, bool approve)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RunSession session;
        lock (_gate)
        {
            if (_active)
            {
                throw new InvalidOperationException("The Rapid PC Use agent is already active.");
            }

            session = _paused ?? throw new InvalidOperationException("No PC agent run is awaiting confirmation.");
            if (session.Handoff is not null)
            {
                throw new InvalidOperationException("The paused run is awaiting outer assistance, not confirmation.");
            }

            var confirmation = session.Confirmation ?? throw new InvalidOperationException("The paused run has no confirmation request.");
            if (!string.Equals(session.RunId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(confirmation.ConfirmationId, confirmationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PC agent confirmation token does not match the paused run.");
            }

            if (_timeProvider.GetUtcNow() > confirmation.ExpiresUtc)
            {
                _paused = null;
                session.Clear();
                var expired = CreateResult(
                    session,
                    PcAgentStatus.Denied,
                    "The pending confirmation expired and the PC task remained stopped.",
                    null,
                    null);
                AgentTelemetry.RunCompleted(
                    session.RunId,
                    expired.Status,
                    session.ModelTurns,
                    session.ActionsExecuted,
                    expired.ElapsedMilliseconds,
                    0,
                    0);
                return expired;
            }

            _paused = null;
            if (!approve)
            {
                session.Clear();
                var denied = CreateResult(session, PcAgentStatus.Denied, "The requested operation was not approved.", null, null);
                AgentTelemetry.RunCompleted(
                    session.RunId,
                    denied.Status,
                    session.ModelTurns,
                    session.ActionsExecuted,
                    denied.ElapsedMilliseconds,
                    0,
                    0);
                return denied;
            }

            if (session.PendingRunbookApprovalStep is { } pendingRunbookStep)
            {
                if (session.ApprovedRunbookStep != pendingRunbookStep)
                {
                    session.ApprovedRunbookStep = pendingRunbookStep;
                    session.ApprovedRunbookRisks.Clear();
                }

                session.ApprovedRunbookRisks.Add(confirmation.Risk);
                session.PendingRunbookApprovalStep = null;
            }
            else
            {
                ClearRunbookApprovals(session);
            }

            // This is surfaced to the inner controller for the next decision.
            // Runbook authority is separately bound to the exact retrieved step.
            session.ApprovedRisk = confirmation.Risk;
            session.Confirmation = null;
            _active = true;
        }

        try
        {
            return ContinueAsync(session).GetAwaiter().GetResult();
        }
        finally
        {
            lock (_gate)
            {
                _active = false;
            }
        }
    }

    internal PcRunResult ResumeHandoff(string sessionId, string handoffId, string outerContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(outerContext) ||
            outerContext.Length > SecurityLimits.MaxAgentOuterContextCharacters ||
            outerContext.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"outer_context must contain 1 to {SecurityLimits.MaxAgentOuterContextCharacters} printable characters.");
        }

        RunSession session;
        lock (_gate)
        {
            if (_active)
            {
                throw new InvalidOperationException("The Rapid PC Use agent is already active.");
            }

            session = _paused ?? throw new InvalidOperationException("No PC agent run is awaiting outer assistance.");
            if (session.Confirmation is not null)
            {
                throw new InvalidOperationException("The paused run is awaiting confirmation, not outer assistance.");
            }

            var handoff = session.Handoff ?? throw new InvalidOperationException("The paused run has no handoff request.");
            if (!string.Equals(session.RunId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(handoff.HandoffId, handoffId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PC agent handoff token does not match the paused run.");
            }

            if (_timeProvider.GetUtcNow() > handoff.ExpiresUtc)
            {
                _paused = null;
                session.Clear();
                var expired = CreateResult(
                    session,
                    PcAgentStatus.Blocked,
                    "The outer-assistance handoff expired and the PC task remained stopped.",
                    null,
                    null);
                AgentTelemetry.RunCompleted(
                    session.RunId,
                    expired.Status,
                    session.ModelTurns,
                    session.ActionsExecuted,
                    expired.ElapsedMilliseconds,
                    0,
                    0);
                return expired;
            }

            _paused = null;
            session.Handoff = null;
            session.PendingOuterContext = outerContext;
            _active = true;
        }

        try
        {
            return ContinueAsync(session).GetAwaiter().GetResult();
        }
        finally
        {
            lock (_gate)
            {
                _active = false;
            }
        }
    }

    internal void CancelPaused()
    {
        RunSession? paused;
        lock (_gate)
        {
            paused = _paused;
            _paused = null;
        }

        paused?.Clear();
        _visualMemory.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPaused();
        _provider.Dispose();
    }

    private async Task<PcRunResult> ContinueAsync(RunSession session)
    {
        var segmentStarted = Stopwatch.GetTimestamp();
        var progress = new ProgressDetector();
        try
        {
            Observation? observation = _desktop.ObserveActiveWindow(beginControl: true);
            if (!session.InitialRetrievalAttempted)
            {
                session.InitialRetrievalAttempted = true;
                try
                {
                    var query = session.Request.Task[..Math.Min(
                        session.Request.Task.Length,
                        SecurityLimits.MaxAgentRetrievalQueryCharacters)];
                    var result = _localRoutes.Search(query);
                    if (result.KnowledgeCount > 0 || result.RunbookCount > 0)
                    {
                        RegisterRetrieval(session, result);
                        session.PendingRetrievedContext = result.Context;
                        session.LastRetrievedContext = result.Context;
                    }

                    AgentTelemetry.KnowledgeRetrieved(session.RunId, 0, result);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
                {
                    // Local memory must be an optimization, never a reason to destroy a visible task.
                    AgentTelemetry.KnowledgeRetrievalUnavailable(session.RunId, 0, exception);
                }
            }

            if (!string.IsNullOrWhiteSpace(session.PendingLaunchUri))
            {
                AgentTelemetry.ObservationCaptured(session.RunId, session.ModelTurns, "pre_launch", observation);
                var launchTiming = _launcher.Launch(session.PendingLaunchUri, _desktop.ThrowIfControlLost);
                session.PendingLaunchUri = "";
                AgentTelemetry.LaunchCompleted(session.RunId, launchTiming);
                if (!launchTiming.TargetActivated)
                {
                    var detail = launchTiming.TargetFound
                        ? $"The direct-launch target opened, but protected foreground process '{launchTiming.ForegroundProcess}' prevented activation."
                        : "The direct-launch target did not expose a visible window within the readiness budget.";
                    return Complete(
                        session,
                        PcAgentStatus.Blocked,
                        $"{detail} No model turn was sent; remove the foreground obstruction or retry without launch_uri.",
                        segmentStarted,
                        null);
                }

                observation = _desktop.ObserveActiveWindow(beginControl: true);
            }

            AgentTelemetry.ObservationCaptured(session.RunId, session.ModelTurns, "initial", observation);
            if (session.ExpectedTopologyKey is not null)
            {
                if (!string.Equals(session.ExpectedTopologyKey, observation.TopologyKey, StringComparison.Ordinal))
                {
                    session.ExpectedTopologyKey = null;
                    return Complete(
                        session,
                        PcAgentStatus.Blocked,
                        "The display layout changed while the PC task was paused, so the task remained stopped.",
                        segmentStarted,
                        null);
                }

                session.ExpectedTopologyKey = null;
            }

            progress.AcceptInitial(observation);
            while (true)
            {
                var activeElapsed = session.ActiveElapsedMilliseconds + ElapsedMilliseconds(segmentStarted);
                if (session.ModelTurns >= session.Request.Limits.MaxModelTurns ||
                    session.ActionsExecuted >= session.Request.Limits.MaxActions ||
                    activeElapsed >= session.Request.Limits.MaxDurationMilliseconds)
                {
                    return Complete(
                        session,
                        PcAgentStatus.LimitReached,
                        "The PC task reached its configured execution limit.",
                        segmentStarted,
                        null);
                }

                var iterationStarted = Stopwatch.GetTimestamp();
                var current = observation ?? throw new InvalidOperationException("The PC agent has no current observation.");
                _visualMemory.Replace(current);
                var turnRequest = new PcModelTurnRequest(
                    session.Request.Task,
                    session.Request.Scope,
                    session.State,
                    session.RecentOutcomes,
                    current,
                    session.ModelTurns + 1,
                    session.Request.Limits.MaxActions - session.ActionsExecuted,
                    session.ApprovedRisk,
                    session.RunId,
                    session.PendingOuterContext,
                    string.IsNullOrWhiteSpace(session.PendingRetrievedContext)
                        ? session.ActiveRunbookContext
                        : session.PendingRetrievedContext);
                var remainingMilliseconds = Math.Max(
                    1,
                    session.Request.Limits.MaxDurationMilliseconds - checked((int)Math.Min(int.MaxValue, activeElapsed)));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(remainingMilliseconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    deadline.Token,
                    _desktop.ControlCancellationToken);

                PcModelTurnResult modelResult;
                var providerStarted = Stopwatch.GetTimestamp();
                try
                {
                    modelResult = await DecideWithRecoveryAsync(
                        session.RunId,
                        session.ModelTurns + 1,
                        turnRequest,
                        linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_desktop.ControlCancellationToken.IsCancellationRequested)
                {
                    _desktop.ThrowIfControlLost();
                    throw;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    return Complete(
                        session,
                        PcAgentStatus.LimitReached,
                        "The PC task reached its configured execution limit.",
                        segmentStarted,
                        null);
                }
                catch (Exception exception) when (IsProviderFailure(exception))
                {
                    return Complete(
                        session,
                        PcAgentStatus.Blocked,
                        "The PC model provider remained unavailable after bounded recovery attempts.",
                        segmentStarted,
                        null);
                }

                var providerCompleted = Stopwatch.GetTimestamp();
                _desktop.ThrowIfControlLost();
                session.ModelTurns++;
                session.PendingOuterContext = "";
                session.PendingRetrievedContext = "";
                AgentTelemetry.ProviderCompleted(session.RunId, session.ModelTurns, modelResult);
                var frameId = current.FrameId;
                observation = null;
                _visualMemory.Clear();

                PreserveRunbookApprovalsOnlyForDecision(session, modelResult.Decision);

                switch (modelResult.Decision)
                {
                    case RetrieveDecision retrieve:
                        session.State = retrieve.NextState;
                        try
                        {
                            var result = _localRoutes.Search(retrieve.Query);
                            RegisterRetrieval(session, result);
                            session.PendingRetrievedContext = result.Context;
                            session.LastRetrievedContext = result.Context;
                            AgentTelemetry.KnowledgeRetrieved(session.RunId, session.ModelTurns, result);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
                        {
                            session.PendingRetrievedContext = "Trusted local retrieval is unavailable. Continue from visible evidence or request one bounded outer handoff; do not retry this lookup.";
                            AgentTelemetry.KnowledgeRetrievalUnavailable(session.RunId, session.ModelTurns, exception);
                        }

                        observation = current;
                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "knowledge_retrieved");
                        break;

                    case RunbookStepDecision runbookStep:
                        var runbookRoute = RouteRunbookStep(
                            session,
                            modelResult,
                            runbookStep,
                            current,
                            iterationStarted,
                            providerStarted,
                            providerCompleted,
                            segmentStarted);
                        if (runbookRoute.TerminalResult is not null)
                        {
                            return runbookRoute.TerminalResult;
                        }

                        observation = runbookRoute.Observation;
                        break;

                    case FinishDecision finish:
                        session.State = finish.FinalState;
                        if (session.PendingRequiredRunbookSteps.Count > 0)
                        {
                            session.PendingRetrievedContext = ExecutePendingRunbookVerifiers(
                                session,
                                current,
                                session.ModelTurns,
                                segmentStarted);
                            session.AddOutcome(new AgentActionOutcome(
                                ["finish"],
                                ScreenChanged: false,
                                "Verify current task state",
                                "The driver rejected completion because a retrieved runbook requires an exact read-only verifier."));
                            observation = current;
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "runbook_verification_required");
                            break;
                        }

                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "finished");
                        return Complete(
                            session,
                            PcAgentStatus.Completed,
                            finish.Summary,
                            segmentStarted,
                            session.Request.ReturnFinalScreenshot ? current : null);

                    case BlockedDecision blocked:
                        session.State = blocked.FinalState;
                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "blocked");
                        return Complete(
                            session,
                            PcAgentStatus.Blocked,
                            blocked.Summary,
                            segmentStarted,
                            null);

                    case ConfirmDecision confirmation:
                        session.State = confirmation.NextState;
                        if (session.ApprovedRisk == confirmation.Risk)
                        {
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "confirmation_repeated");
                            return Complete(
                                session,
                                PcAgentStatus.Blocked,
                                "The inner controller repeated a confirmation request that was already approved.",
                                segmentStarted,
                                null);
                        }

                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "confirmation_requested");
                        return Pause(session, confirmation.Risk, confirmation.OperationSummary, current.TopologyKey, segmentStarted);

                    case HandoffDecision handoff:
                        session.State = handoff.NextState;
                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "outer_handoff_requested");
                        return PauseForHandoff(session, handoff, current.TopologyKey, segmentStarted);

                    case ActDecision act:
                        session.State = act.NextState;
                        var policyStarted = Stopwatch.GetTimestamp();
                        var policy = _policy.Evaluate(act, session.Request.Scope, session.ApprovedRisk);
                        AgentTelemetry.PolicyEvaluated(
                            session.RunId,
                            session.ModelTurns,
                            act.Actions.GetArrayLength(),
                            (long)(Stopwatch.GetElapsedTime(policyStarted).TotalMilliseconds * 1_000));
                        if (policy.BlockReason is not null)
                        {
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "policy_blocked");
                            return Complete(
                                session,
                                PcAgentStatus.Blocked,
                                policy.BlockReason,
                                segmentStarted,
                                null);
                        }

                        if (policy.ConfirmationRisk is PcRiskFlag risk)
                        {
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "policy_confirmation");
                            return Pause(session, risk, ConfirmationSummary(risk), current.TopologyKey, segmentStarted);
                        }

                        if (!policy.Allowed)
                        {
                            throw new InvalidOperationException("The action policy returned an invalid result.");
                        }

                        var actionCount = act.Actions.GetArrayLength();
                        if (actionCount > session.Request.Limits.MaxActions - session.ActionsExecuted)
                        {
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "action_limit");
                            return Complete(
                                session,
                                PcAgentStatus.LimitReached,
                                "The PC task reached its configured action limit.",
                                segmentStarted,
                                null);
                        }

                        DesktopActResult actResult;
                        try
                        {
                            actResult = _desktop.Act(frameId, act.Actions, settleMilliseconds: 0, observeAfter: true);
                        }
                        catch (PcActionPlanValidationException exception)
                        {
                            session.AddOutcome(new AgentActionOutcome(
                                ActionTypes(act.Actions),
                                ScreenChanged: false,
                                act.ExpectedChange,
                                ValidationFeedback(exception)));
                            AgentTelemetry.ActionRejected(session.RunId, session.ModelTurns, exception);
                            observation = current;
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "action_rejected");
                            break;
                        }
                        catch (StaleFrameException)
                        {
                            AgentTelemetry.Recovery(session.RunId, session.ModelTurns, "frame_refresh", 1);
                            session.AddOutcome(new AgentActionOutcome(
                                ActionTypes(act.Actions),
                                ScreenChanged: false,
                                act.ExpectedChange,
                                "The frame became unusable before input. A fresh screenshot was captured; replan from it."));
                            observation = _desktop.ObserveActiveWindow(beginControl: true);
                            AgentTelemetry.ObservationCaptured(session.RunId, session.ModelTurns, "frame_refresh", observation);
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "frame_refreshed");
                            break;
                        }

                        var completedActionCount = actResult.Failure?.CompletedActions ?? actionCount;
                        session.ActionsExecuted += completedActionCount;
                        if (completedActionCount > 0 && HasConsequentialNativeAction(act.Actions, completedActionCount))
                        {
                            RearmActiveRunbookVerifiers(session);
                        }
                        if (policy.ConsumedApprovedRisk)
                        {
                            session.ApprovedRisk = null;
                        }

                        observation = actResult.Observation ?? throw new InvalidOperationException("The agent action did not return a fresh observation.");
                        if (actResult.Failure is not null)
                        {
                            AgentTelemetry.Recovery(session.RunId, session.ModelTurns, "native_action", 1);
                            session.AddOutcome(new AgentActionOutcome(
                                ActionTypes(act.Actions),
                                ScreenChanged: true,
                                act.ExpectedChange,
                                $"Action {actResult.Failure.ActionIndex} ({actResult.Failure.ActionType}) was interrupted after {actResult.Failure.CompletedActions} earlier actions. Continue from the current screenshot using a different approach."));
                            RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "action_interrupted");
                            break;
                        }

                        var progressResult = progress.Evaluate(observation, act.Actions);
                        session.AddOutcome(new AgentActionOutcome(
                            ActionTypes(act.Actions),
                            progressResult.ScreenChanged,
                            act.ExpectedChange));
                        AgentTelemetry.IterationCompleted(session.RunId, session.ModelTurns, actResult, progressResult);
                        if (!string.IsNullOrWhiteSpace(act.CompletionGuardText))
                        {
                            var guardResult = _completionGuard.Verify(act.CompletionGuardText);
                            AgentTelemetry.CompletionGuardEvaluated(session.RunId, session.ModelTurns, guardResult);
                            if (guardResult.Matched)
                            {
                                if (session.PendingRequiredRunbookSteps.Count > 0)
                                {
                                    session.PendingRetrievedContext = ExecutePendingRunbookVerifiers(
                                        session,
                                        observation,
                                        session.ModelTurns,
                                        segmentStarted);
                                    session.AddOutcome(new AgentActionOutcome(
                                        ["completion_guard"],
                                        ScreenChanged: true,
                                        "Verify current task state",
                                        "The driver retained control because a retrieved runbook requires an exact read-only verifier."));
                                    RecordDecisionRoute(
                                        session,
                                        modelResult,
                                        iterationStarted,
                                        providerStarted,
                                        providerCompleted,
                                        "completion_guard_verification_required");
                                    break;
                                }

                                RecordDecisionRoute(
                                    session,
                                    modelResult,
                                    iterationStarted,
                                    providerStarted,
                                    providerCompleted,
                                    "completion_guard_matched");
                                return Complete(
                                    session,
                                    PcAgentStatus.Completed,
                                    act.CompletionSummary,
                                    segmentStarted,
                                    session.Request.ReturnFinalScreenshot ? observation : null);
                            }
                        }

                        if (progressResult.ConsecutiveNoProgressTurns >= session.Request.Limits.MaxConsecutiveNoProgressTurns)
                        {
                            AgentTelemetry.Recovery(session.RunId, session.ModelTurns, "no_progress", 1);
                            session.AddOutcome(new AgentActionOutcome(
                                ActionTypes(act.Actions),
                                ScreenChanged: false,
                                act.ExpectedChange,
                                "Repeated actions made no visible progress. Do not repeat them; inspect for a modal, change focus, use Escape, or choose another target."));
                            progress.ResetNoProgress();
                        }

                        RecordDecisionRoute(session, modelResult, iterationStarted, providerStarted, providerCompleted, "action_completed");
                        break;

                    default:
                        throw new InvalidOperationException("The provider returned an unsupported decision.");
                }
            }
        }
        catch (UserTakeoverException)
        {
            var elapsed = session.ActiveElapsedMilliseconds + ElapsedMilliseconds(segmentStarted);
            _visualMemory.Clear();
            progress.Clear();
            AgentTelemetry.RunCompleted(
                session.RunId,
                PcAgentStatus.UserTakeover,
                session.ModelTurns,
                session.ActionsExecuted,
                elapsed,
                0,
                0);
            session.Clear();
            throw;
        }
        catch
        {
            var elapsed = session.ActiveElapsedMilliseconds + ElapsedMilliseconds(segmentStarted);
            _visualMemory.Clear();
            progress.Clear();
            AgentTelemetry.RunCompleted(
                session.RunId,
                PcAgentStatus.Failed,
                session.ModelTurns,
                session.ActionsExecuted,
                elapsed,
                0,
                0);
            session.Clear();
            try
            {
                _desktop.Stop();
            }
            catch
            {
                // The MCP boundary records cleanup failures through its existing terminal path.
            }

            throw;
        }
    }

    private PcRunResult Pause(
        RunSession session,
        PcRiskFlag risk,
        string summary,
        string topologyKey,
        long segmentStarted)
    {
        _desktop.ThrowIfControlLost();
        session.ActiveElapsedMilliseconds += ElapsedMilliseconds(segmentStarted);
        session.Confirmation = new PcConfirmation(
            DriverLog.NewOperationId("confirm"),
            summary,
            risk,
            _timeProvider.GetUtcNow() + SecurityLimits.AgentConfirmationLifetime);
        session.ExpectedTopologyKey = topologyKey;
        _visualMemory.Clear();
        _desktop.Stop();
        lock (_gate)
        {
            _paused = session;
        }

        var result = CreateResult(
            session,
            PcAgentStatus.NeedsConfirmation,
            "The PC task is waiting for confirmation in the main Codex conversation.",
            session.Confirmation,
            null);
        AgentTelemetry.RunCompleted(
            session.RunId,
            result.Status,
            session.ModelTurns,
            session.ActionsExecuted,
            result.ElapsedMilliseconds,
            0,
            StateBytes(session.State));
        return result;
    }

    private PcRunResult PauseForHandoff(
        RunSession session,
        HandoffDecision decision,
        string topologyKey,
        long segmentStarted)
    {
        _desktop.ThrowIfControlLost();
        session.ActiveElapsedMilliseconds += ElapsedMilliseconds(segmentStarted);
        session.Handoff = new PcHandoff(
            DriverLog.NewOperationId("handoff"),
            decision.Reason,
            decision.Request,
            _timeProvider.GetUtcNow() + SecurityLimits.AgentHandoffLifetime);
        // A one-shot confirmation is bound to the exact pending action. Never
        // carry it across an outer-planner boundary where the plan may change.
        session.ApprovedRisk = null;
        ClearRunbookApprovals(session);
        session.ExpectedTopologyKey = topologyKey;
        _visualMemory.Clear();
        _desktop.Stop();
        lock (_gate)
        {
            _paused = session;
        }

        var result = CreateResult(
            session,
            PcAgentStatus.NeedsHandoff,
            "The PC task is waiting for bounded assistance from the outer Codex planner.",
            null,
            null,
            session.Handoff);
        AgentTelemetry.RunCompleted(
            session.RunId,
            result.Status,
            session.ModelTurns,
            session.ActionsExecuted,
            result.ElapsedMilliseconds,
            0,
            StateBytes(session.State));
        return result;
    }

    private PcRunResult Complete(
        RunSession session,
        PcAgentStatus status,
        string summary,
        long segmentStarted,
        Observation? finalObservation)
    {
        _desktop.ThrowIfControlLost();
        session.ActiveElapsedMilliseconds += ElapsedMilliseconds(segmentStarted);
        _visualMemory.Clear();
        _desktop.Stop();
        var routeLearningStarted = Stopwatch.GetTimestamp();
        try
        {
            _localRoutes.RecordPerformance(session.RunbookExecutionSamples);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            // Route history is an optimization and must never change the task result.
            AgentTelemetry.RouteLearningUnavailable(session.RunId, exception);
        }

        var routeLearningMicroseconds = ElapsedMicroseconds(routeLearningStarted, Stopwatch.GetTimestamp());
        session.ActiveElapsedMilliseconds += (long)Math.Ceiling(routeLearningMicroseconds / 1_000d);
        AgentTelemetry.RouteLearningCompleted(
            session.RunId,
            session.RunbookExecutionSamples.Count,
            routeLearningMicroseconds);
        var result = CreateResult(session, status, summary, null, finalObservation);

        session.Clear();
        AgentTelemetry.RunCompleted(
            session.RunId,
            result.Status,
            result.ModelTurns,
            result.ActionsExecuted,
            result.ElapsedMilliseconds,
            0,
            0);
        return result;
    }

    private static PcRunResult CreateResult(
        RunSession session,
        PcAgentStatus status,
        string summary,
        PcConfirmation? confirmation,
        Observation? finalObservation,
        PcHandoff? handoff = null)
        => new(
            status,
            session.RunId,
            BoundSummary(summary),
            session.ModelTurns,
            session.ActionsExecuted,
            session.ActiveElapsedMilliseconds,
            session.TelemetrySessionId,
            confirmation,
            handoff,
            finalObservation);

    private void ValidateRequest(PcRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Task) || request.Task.Length > SecurityLimits.MaxAgentTaskCharacters)
        {
            throw new ArgumentException($"task must contain 1 to {SecurityLimits.MaxAgentTaskCharacters} characters.");
        }

        if (request.ExecutionContext.Length > SecurityLimits.MaxAgentOuterContextCharacters ||
            request.ExecutionContext.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"execution_context must contain at most {SecurityLimits.MaxAgentOuterContextCharacters} printable characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.LaunchUri))
        {
            _ = PcLaunchCoordinator.ValidateUri(request.LaunchUri);
        }

        if (request.Scope.AllowedProcesses.Count > SecurityLimits.MaxAgentAllowedProcesses)
        {
            throw new ArgumentException("allowed_processes contains too many entries.");
        }

        foreach (var process in request.Scope.AllowedProcesses)
        {
            _ = ActionPolicy.NormalizeProcessName(process);
        }

        if (request.Limits.MaxModelTurns is < 1 or > SecurityLimits.MaxAgentModelTurns ||
            request.Limits.MaxModelTurns > _options.MaxModelTurns ||
            request.Limits.MaxActions is < 1 or > SecurityLimits.MaxAgentActions ||
            request.Limits.MaxActions > _options.MaxActions ||
            request.Limits.MaxDurationMilliseconds is < 10_000 or > SecurityLimits.MaxAgentDurationMilliseconds ||
            request.Limits.MaxDurationMilliseconds > _options.MaxDurationMilliseconds ||
            request.Limits.MaxConsecutiveNoProgressTurns is < 1 or > SecurityLimits.MaxAgentNoProgressTurns ||
            request.Limits.MaxConsecutiveNoProgressTurns > _options.MaxConsecutiveNoProgressTurns)
        {
            throw new ArgumentException("Requested agent limits may not exceed the configured ceilings.");
        }
    }

    private static string[] ActionTypes(JsonElement actions)
        => actions.EnumerateArray()
            .Select(action => action.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? "unknown"
                : "unknown")
            .ToArray();

    private async Task<PcModelTurnResult> DecideWithRecoveryAsync(
        string runId,
        int turn,
        PcModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var attemptStarted = Stopwatch.GetTimestamp();
            try
            {
                return await _provider.DecideAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsProviderFailure(exception))
            {
                var willRetry = attempt < attempts && !cancellationToken.IsCancellationRequested;
                AgentTelemetry.ProviderAttemptFailed(
                    runId,
                    turn,
                    _provider,
                    attempt,
                    attempts,
                    willRetry,
                    ElapsedMicroseconds(attemptStarted, Stopwatch.GetTimestamp()),
                    exception);
                if (!willRetry)
                {
                    throw;
                }

                AgentTelemetry.Recovery(runId, turn, "provider", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsProviderFailure(Exception exception)
        => exception switch
        {
            PcAgentProviderException provider =>
                provider.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                provider.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                (int)provider.StatusCode >= 500,
            PcModelProviderStageException staged when staged.InnerException is not null => IsProviderFailure(staged.InnerException),
            HttpRequestException or IOException or InvalidOperationException or JsonException => true,
            _ => false,
        };

    private static string ValidationFeedback(PcActionPlanValidationException exception)
    {
        var location = exception.ActionIndex.HasValue
            ? $"action {exception.ActionIndex.Value}{(string.IsNullOrWhiteSpace(exception.ActionType) ? string.Empty : $" ({exception.ActionType})")}: "
            : string.Empty;
        var range = exception.Field is not null && exception.AllowedMinimum.HasValue && exception.AllowedMaximum.HasValue
            ? $" Use {exception.Field} between {exception.AllowedMinimum.Value} and {exception.AllowedMaximum.Value}."
            : string.Empty;
        return $"The driver rejected {location}{exception.SafeMessage}{range} No action executed; correct the batch using the current screenshot.";
    }

    private static string ConfirmationSummary(PcRiskFlag risk) => risk switch
    {
        PcRiskFlag.LocalProcessLaunch => "Allow the PC agent to launch a trusted local process?",
        PcRiskFlag.ExternalCommunication => "Allow the PC agent to send or submit information externally?",
        PcRiskFlag.RemoteContentChange => "Allow the PC agent to modify or delete remote content or social state?",
        PcRiskFlag.LocalDeletion => "Allow the PC agent to delete a local item?",
        PcRiskFlag.CredentialEntry => "Allow the PC agent to enter credentials?",
        PcRiskFlag.PurchaseOrFinancial => "Allow the PC agent to perform a purchase or financial action?",
        PcRiskFlag.AccountOrPermissionChange => "Allow the PC agent to change an account or permission?",
        PcRiskFlag.DownloadOrInstall => "Allow the PC agent to download or install software?",
        PcRiskFlag.UnclassifiedSensitiveAction => "Allow the PC agent to perform the pending sensitive action?",
        _ => "Allow the pending sensitive action?",
    };

    private static PcRiskFlag? RequiredRunbookRisk(string effect)
        => effect == "none"
            ? null
            : PcAgentDecisionParser.TryParseRisk(effect, out var risk)
                ? risk
                : throw new InvalidOperationException("The trusted runbook step declares an unsupported effect.");

    private static bool ScopeAllowsRisk(PcRunScope scope, PcRiskFlag risk) => risk switch
    {
        PcRiskFlag.LocalProcessLaunch => scope.AllowLocalProcessLaunches,
        PcRiskFlag.ExternalCommunication => scope.AllowExternalCommunication,
        PcRiskFlag.RemoteContentChange => scope.AllowRemoteContentChanges,
        PcRiskFlag.LocalDeletion => scope.AllowLocalDeletion,
        _ => false,
    };

    private static string BoundSummary(string summary)
        => summary.Length <= SecurityLimits.MaxAgentSummaryCharacters
            ? summary
            : summary[..SecurityLimits.MaxAgentSummaryCharacters];

    private static int StateBytes(AgentWorkingState state)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(state));

    private static long ElapsedMilliseconds(long started)
        => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void RecordDecisionRoute(
        RunSession session,
        PcModelTurnResult modelResult,
        long iterationStarted,
        long providerStarted,
        long providerCompleted,
        string outcome)
    {
        var completed = Stopwatch.GetTimestamp();
        AgentTelemetry.DecisionRouted(
            session.RunId,
            session.ModelTurns,
            modelResult.Decision.Kind.ToString().ToLowerInvariant(),
            outcome,
            ElapsedMicroseconds(iterationStarted, providerStarted),
            ElapsedMicroseconds(providerStarted, providerCompleted),
            ElapsedMicroseconds(providerCompleted, completed),
            ElapsedMicroseconds(iterationStarted, completed));
    }

    private static long ElapsedMicroseconds(long started, long completed)
        => (long)(Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds * 1_000);

    private sealed class RunSession(PcRunRequest request)
    {
        internal string RunId { get; } = DriverLog.NewOperationId("run");
        internal string TelemetrySessionId { get; } = DriverLog.NewOperationId("agent");
        internal PcRunRequest Request { get; } = request;
        internal AgentWorkingState State { get; set; } = AgentWorkingState.Empty;
        internal List<AgentActionOutcome> RecentOutcomes { get; } = [];
        internal int ModelTurns { get; set; }
        internal int ActionsExecuted { get; set; }
        internal long ActiveElapsedMilliseconds { get; set; }
        internal PcRiskFlag? ApprovedRisk { get; set; }
        internal PcRunbookStepReference? ApprovedRunbookStep { get; set; }
        internal PcRunbookStepReference? PendingRunbookApprovalStep { get; set; }
        internal HashSet<PcRiskFlag> ApprovedRunbookRisks { get; } = [];
        internal PcConfirmation? Confirmation { get; set; }
        internal PcHandoff? Handoff { get; set; }
        internal string PendingOuterContext { get; set; } = request.ExecutionContext;
        internal string PendingRetrievedContext { get; set; } = "";
        internal string ActiveRunbookContext { get; set; } = "";
        internal string PendingLaunchUri { get; set; } = request.LaunchUri;
        internal string? ExpectedTopologyKey { get; set; }
        internal HashSet<string> RetrievedRunbookKeys { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> ActiveRunbookKeys { get; } = new(StringComparer.Ordinal);
        internal Dictionary<PcRunbookStepReference, PcRunbookStep> RetrievedRunbookSteps { get; } = [];
        internal HashSet<PcRunbookStepReference> PendingRequiredRunbookSteps { get; } = [];
        internal HashSet<PcRunbookStepReference> NonRepeatableRunbookStepAttempts { get; } = [];
        internal Dictionary<PcRunbookStepReference, int> LastSuccessfulRunbookStepActionCounts { get; } = [];
        internal Dictionary<PcRunbookStepReference, int> LastFailedRunbookStepActionCounts { get; } = [];
        internal List<PcRunbookExecutionSample> RunbookExecutionSamples { get; } = [];
        internal bool InitialRetrievalAttempted { get; set; }
        internal string LastRetrievedContext { get; set; } = "";

        internal void AddOutcome(AgentActionOutcome outcome)
        {
            RecentOutcomes.Add(outcome);
            while (RecentOutcomes.Count > SecurityLimits.MaxAgentRecentOutcomes)
            {
                RecentOutcomes.RemoveAt(0);
            }
        }

        internal void Clear()
        {
            State = AgentWorkingState.Empty;
            RecentOutcomes.Clear();
            ApprovedRisk = null;
            ApprovedRunbookStep = null;
            PendingRunbookApprovalStep = null;
            ApprovedRunbookRisks.Clear();
            Confirmation = null;
            Handoff = null;
            PendingOuterContext = "";
            PendingRetrievedContext = "";
            ActiveRunbookContext = "";
            PendingLaunchUri = "";
            ExpectedTopologyKey = null;
            RetrievedRunbookKeys.Clear();
            ActiveRunbookKeys.Clear();
            RetrievedRunbookSteps.Clear();
            PendingRequiredRunbookSteps.Clear();
            NonRepeatableRunbookStepAttempts.Clear();
            LastSuccessfulRunbookStepActionCounts.Clear();
            LastFailedRunbookStepActionCounts.Clear();
            RunbookExecutionSamples.Clear();
            LastRetrievedContext = "";
        }
    }
}
