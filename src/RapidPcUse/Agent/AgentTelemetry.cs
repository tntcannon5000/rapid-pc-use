namespace RapidPcUse.Agent;

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
                timings_us = result.Timings,
                usage = result.Usage,
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
                capture_total_us = actResult.Observation?.Frames.Sum(frame => frame.Timings.TotalMicroseconds),
            });

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
        PcAgentStatus.Blocked => "blocked",
        PcAgentStatus.LimitReached => "limit_reached",
        PcAgentStatus.Failed => "failed",
        PcAgentStatus.Denied => "denied",
        PcAgentStatus.UserTakeover => "user_takeover",
        _ => "failed",
    };
}
