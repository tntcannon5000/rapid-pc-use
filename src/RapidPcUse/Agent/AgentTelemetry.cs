namespace RapidPcUse.Agent;

using RapidPcUse.Knowledge;

internal static class AgentTelemetry
{
    internal static void RunStarted(string runId, IPcModelProvider provider, PcRunRequest request)
        => DriverLog.Info(
            "agent.run_started",
            "The internal PC agent loop started.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                provider = provider.Name,
                model = provider.Model,
                max_model_turns = request.Limits.MaxModelTurns,
                max_actions = request.Limits.MaxActions,
                max_duration_ms = request.Limits.MaxDurationMilliseconds,
                no_progress_limit = request.Limits.MaxConsecutiveNoProgressTurns,
                execution_context_characters = request.ExecutionContext.Length,
                direct_launch_requested = !string.IsNullOrWhiteSpace(request.LaunchUri),
                allow_local_process_launches = request.Scope.AllowLocalProcessLaunches,
            });

    internal static void KnowledgeRetrieved(string runId, int turn, PcRouteRetrievalResult result)
        => DriverLog.Info(
            "agent.knowledge_retrieved",
            "The inner PC loop retrieved bounded trusted local route context.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                knowledge_count = result.KnowledgeCount,
                runbook_count = result.RunbookCount,
                context_characters = result.Context.Length,
                elapsed_us = result.ElapsedMicroseconds,
                privacy = "Query, keys, paths, and retrieved contents omitted.",
            });

    internal static void KnowledgeRetrievalUnavailable(string runId, int turn, Exception exception)
        => DriverLog.Warning(
            "agent.knowledge_retrieval_unavailable",
            "The inner PC loop could not read trusted local route context and continued without destroying the desktop run.",
            operationId: runId,
            tool: "pc_run",
            data: new { turn },
            exception: exception);

    internal static void RouteLearningUnavailable(string runId, Exception exception)
        => DriverLog.Warning(
            "agent.route_learning_unavailable",
            "The completed run could not persist privacy-safe route performance; the task result was preserved.",
            operationId: runId,
            tool: "pc_run",
            data: new { privacy = "Runbook keys, step IDs, targets, arguments, and contents omitted." },
            exception: exception);

    internal static void RouteLearningCompleted(string runId, int sampleCount, long elapsedMicroseconds)
        => DriverLog.Info(
            "agent.route_learning_completed",
            "The terminal path finished privacy-safe route-performance persistence.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                sample_count = sampleCount,
                elapsed_us = elapsedMicroseconds,
                privacy = "Runbook keys, step IDs, targets, arguments, and contents omitted.",
            });

    internal static void RunbookStepExecuted(
        string runId,
        int turn,
        PcRunbookExecutionResult result,
        Observation observation)
        => DriverLog.Info(
            "agent.runbook_step_executed",
            "The inner PC loop executed one exact trusted local runbook step.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                step_kind = result.StepKind,
                dispatch_us = result.DispatchMicroseconds,
                readiness_us = result.ReadinessMicroseconds,
                total_us = result.TotalMicroseconds,
                target_observed = result.TargetObserved,
                reused_existing_target = result.ReusedExistingTarget,
                foreground_process = result.ForegroundProcess,
                capture_total_us = CaptureWallMicroseconds(observation),
                privacy = "Runbook key, step ID, description, target, request body, and result data omitted.",
            });

    internal static void RunbookStepUnavailable(string runId, int turn, Exception exception)
        => DriverLog.Warning(
            "agent.runbook_step_unavailable",
            "The trusted runbook step was unavailable; no retry was attempted.",
            operationId: runId,
            tool: "pc_run",
            data: new { turn, privacy = "Runbook key, step ID, path, URL, body, and result omitted." },
            exception: exception);

    internal static void RunbookStepAttemptFailed(
        string runId,
        int turn,
        string stepKind,
        bool effectful,
        long totalMicroseconds,
        Exception exception)
        => DriverLog.Warning(
            "agent.runbook_step_attempt_failed",
            effectful
                ? "A trusted effectful runbook attempt failed or returned ambiguously after consuming its action budget; it remains non-repeatable."
                : "A trusted read-only runbook attempt failed after consuming its action budget.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                step_kind = stepKind,
                effectful,
                total_us = totalMicroseconds,
                action_budget_consumed = true,
                privacy = "Runbook key, step ID, path, URL, body, and result omitted.",
            },
            exception: exception);

    internal static void RunbookStepEffectUncertain(
        string runId,
        int turn,
        PcRunbookExecutionResult result,
        Observation observation)
        => DriverLog.Warning(
            "agent.runbook_step_effect_uncertain",
            "A trusted effectful runbook request was dispatched but its result could not be verified; action budget and one-shot authority were consumed and the step remains non-repeatable.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                step_kind = result.StepKind,
                dispatch_us = result.DispatchMicroseconds,
                total_us = result.TotalMicroseconds,
                action_budget_consumed = true,
                target_observed = false,
                capture_total_us = CaptureWallMicroseconds(observation),
                privacy = "Runbook key, step ID, target, request body, and result data omitted.",
            });

    internal static void LaunchCompleted(string runId, PcLaunchTiming timing)
        => DriverLog.Info(
            "agent.launch_completed",
            "Rapid PC Use completed a trusted direct launch and bounded foreground-readiness wait.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                scheme = timing.Scheme,
                dispatch_us = timing.DispatchMicroseconds,
                readiness_wait_us = timing.ReadinessWaitMicroseconds,
                total_us = timing.TotalMicroseconds,
                target_found = timing.TargetFound,
                target_activated = timing.TargetActivated,
                foreground_process = timing.ForegroundProcess,
            });

    internal static void ProviderCompleted(string runId, int turn, PcModelTurnResult result)
        => DriverLog.Info(
            "agent.provider_completed",
            "The internal model returned a bounded PC decision.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                provider = result.Provider,
                model = result.Model,
                decision = result.Decision.Kind.ToString().ToLowerInvariant(),
                request_bytes = result.RequestBytes,
                image_count = result.ImageCount,
                image_bytes = result.ImageBytes,
                parse_us = result.ParseMicroseconds,
                local_timings_us = result.LocalTimings,
                timings_us = result.Timings,
                usage = result.Usage,
            });

    internal static void PolicyEvaluated(string runId, int turn, int actionCount, long elapsedMicroseconds)
        => DriverLog.Info(
            "agent.policy_evaluated",
            "The internal PC agent evaluated the local action policy.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                action_count = actionCount,
                elapsed_us = elapsedMicroseconds,
            });

    internal static void CompletionGuardEvaluated(string runId, int turn, CompletionGuardResult result)
        => DriverLog.Info(
            "agent.completion_guard_evaluated",
            "The internal PC agent evaluated a bounded local completion guard.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                matched = result.Matched,
                elements_inspected = result.ElementsInspected,
                elapsed_us = result.ElapsedMicroseconds,
                outcome = result.Outcome,
            });

    internal static void DecisionRouted(
        string runId,
        int turn,
        string decision,
        string outcome,
        long preparationMicroseconds,
        long providerWallMicroseconds,
        long routeMicroseconds,
        long iterationMicroseconds)
        => DriverLog.Info(
            "agent.decision_routed",
            "The internal PC agent finished routing one model decision.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                decision,
                outcome,
                preparation_us = preparationMicroseconds,
                provider_wall_us = providerWallMicroseconds,
                route_us = routeMicroseconds,
                iteration_us = iterationMicroseconds,
            });

    internal static void ObservationCaptured(string runId, int turn, string phase, Observation observation)
        => DriverLog.Info(
            "agent.observation_captured",
            "The internal PC agent captured visible state.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                phase,
                capture_scope = observation.CaptureScope,
                display_count = observation.Frames.Count,
                capture_total_us = CaptureWallMicroseconds(observation),
                displays = observation.Frames.Select(frame => new
                {
                    display_id = frame.Monitor.Id,
                    primary = frame.Monitor.IsPrimary,
                    origin_x = frame.Monitor.Left,
                    origin_y = frame.Monitor.Top,
                    native_width = frame.Monitor.Width,
                    native_height = frame.Monitor.Height,
                    encoded_width = frame.EncodedWidth,
                    encoded_height = frame.EncodedHeight,
                    capture_timings_us = frame.Timings,
                }).ToArray(),
            });

    internal static void IterationCompleted(
        string runId,
        int turn,
        DesktopActResult actResult,
        ProgressObservation progress)
        => DriverLog.Info(
            "agent.iteration_completed",
            "The internal PC agent completed an action iteration.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                action_count = actResult.Actions.Count,
                action_execution_us = actResult.Actions.Sum(action => action.ElapsedMicroseconds),
                actions = actResult.Actions,
                settle_requested_ms = actResult.SettleRequestedMilliseconds,
                settle_elapsed_us = actResult.SettleElapsedMicroseconds,
                screen_changed = progress.ScreenChanged,
                visual_difference = progress.VisualDifference,
                repeated_action = progress.RepeatedAction,
                consecutive_no_progress_turns = progress.ConsecutiveNoProgressTurns,
                capture_total_us = CaptureWallMicroseconds(actResult.Observation),
                capture_parallel_worker_total_us = actResult.Observation?.Frames.Sum(frame => frame.Timings.TotalMicroseconds),
            });

    internal static long? CaptureWallMicroseconds(Observation? observation)
        => observation is null ? null : checked(observation.TotalMilliseconds * 1_000);

    internal static void ActionRejected(
        string runId,
        int turn,
        PcActionPlanValidationException exception)
        => DriverLog.Warning(
            "agent.action_rejected",
            "The internal PC agent action batch was rejected before native input and will be corrected on the next model turn.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                rejection = exception.SafeData(),
            });

    internal static void Recovery(string runId, int turn, string category, int attempt)
        => DriverLog.Warning(
            "agent.recovery",
            "The internal PC loop recovered without returning control to the outer model.",
            operationId: runId,
            tool: "pc_run",
            data: new { turn, category, attempt });

    internal static void ProviderAttemptFailed(
        string runId,
        int turn,
        IPcModelProvider provider,
        int attempt,
        int maximumAttempts,
        bool willRetry,
        long elapsedMicroseconds,
        Exception exception)
    {
        var failure = ProviderFailureDiagnostics.Capture(exception);
        DriverLog.Warning(
            "agent.provider_attempt_failed",
            "The internal model provider failed during a bounded decision attempt.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                turn,
                provider = provider.Name,
                model = provider.Model,
                attempt,
                maximum_attempts = maximumAttempts,
                will_retry = willRetry,
                elapsed_us = elapsedMicroseconds,
                stage = failure.Stage,
                reason_code = failure.ReasonCode,
                exception_type = failure.ExceptionType,
                hresult = failure.HResult,
                privacy = "Prompts, screenshots, credentials, response bodies, and exception messages omitted.",
            },
            exception: exception);
    }

    internal static void RunCompleted(
        string runId,
        PcAgentStatus status,
        int turns,
        int actions,
        long elapsedMilliseconds,
        int retainedImages,
        int retainedStateBytes)
        => DriverLog.Info(
            "agent.run_completed",
            "The internal PC agent loop returned to the main Codex model.",
            operationId: runId,
            tool: "pc_run",
            data: new
            {
                status = StatusName(status),
                model_turns = turns,
                actions_executed = actions,
                elapsed_ms = elapsedMilliseconds,
                retained_images = retainedImages,
                retained_state_bytes = retainedStateBytes,
            });

    private static string StatusName(PcAgentStatus status) => status switch
    {
        PcAgentStatus.Completed => "completed",
        PcAgentStatus.NeedsConfirmation => "needs_confirmation",
        PcAgentStatus.NeedsHandoff => "needs_handoff",
        PcAgentStatus.Blocked => "blocked",
        PcAgentStatus.LimitReached => "limit_reached",
        PcAgentStatus.Failed => "failed",
        PcAgentStatus.Denied => "denied",
        PcAgentStatus.UserTakeover => "user_takeover",
        _ => "failed",
    };
}
