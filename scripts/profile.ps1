[CmdletBinding()]
param(
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'RapidPcUse\rapid-pc-use.log'),
    [string]$SessionId,
    [string]$AgentRunId,
    [string]$CodexRolloutPath,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Get-ProfilePercentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) {
        return $null
    }

    $ordered = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1)
    return [double]$ordered[$index]
}

if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    throw "Rapid PC Use log not found: $LogPath"
}

$entries = @(
    Get-Content -LiteralPath $LogPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object {
            try { $_ | ConvertFrom-Json }
            catch { $null }
        } |
        Where-Object { $null -ne $_ }
)

if ([string]::IsNullOrWhiteSpace($SessionId) -and -not [string]::IsNullOrWhiteSpace($AgentRunId)) {
    $agentEntry = $entries |
        Where-Object { $_.operation_id -eq $AgentRunId -and $_.event -like 'agent.*' } |
        Select-Object -Last 1
    if ($null -ne $agentEntry) {
        $SessionId = [string]$agentEntry.session_id
    }
}

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    $SessionId = [string]($entries | Select-Object -Last 1).session_id
}

$session = @($entries | Where-Object { $_.session_id -eq $SessionId })
if ($session.Count -eq 0) {
    throw "No log entries found for session '$SessionId'."
}

$startsByOperation = @{}
$responsesByOperation = @{}
foreach ($entry in $session) {
    if ($entry.event -eq 'tool.started' -and $entry.operation_id) {
        $startsByOperation[[string]$entry.operation_id] = $entry
    }
    elseif ($entry.event -eq 'mcp.response_written' -and $entry.operation_id) {
        $responsesByOperation[[string]$entry.operation_id] = $entry
    }
}

$actions = [Collections.Generic.List[object]]::new()
$calls = [Collections.Generic.List[object]]::new()
foreach ($completed in @($session | Where-Object { $_.event -eq 'tool.completed' })) {
    $operationId = [string]$completed.operation_id
    $start = $startsByOperation[$operationId]
    $response = $responsesByOperation[$operationId]
    $result = $completed.data.result
    $actionRows = @($result.actions | Where-Object { $null -ne $_ })
    foreach ($action in $actionRows) {
        $actions.Add([pscustomobject]@{
            Sequence = $start.data.sequence
            Tool = $completed.tool
            Action = $action.index
            Type = $action.type
            ElapsedMs = [Math]::Round(([double]$action.elapsed_microseconds / 1000), 3)
            RequestedWaitMs = $action.requested_wait_milliseconds
            TypedCodeUnits = $action.typed_code_units
            TypeIntervalMs = $action.type_interval_milliseconds
            PointerPacingMs = $action.pointer_pacing_milliseconds
        })
    }

    $observation = if ($completed.tool -eq 'pc_act') { $result.observation } else { $result }
    $calls.Add([pscustomobject]@{
        Sequence = $start.data.sequence
        Tool = $completed.tool
        LlmOrchestrationGapMs = if ($null -eq $start.data.previous_response_to_request_us) { $null } else { [Math]::Round(([double]$start.data.previous_response_to_request_us / 1000), 3) }
        DriverMs = [double]$completed.data.elapsed_ms
        Actions = $actionRows.Count
        ActionExecutionMs = if ($null -eq $result.action_execution_us) { $null } else { [Math]::Round(([double]$result.action_execution_us / 1000), 3) }
        SettleRequestedMs = $result.settle_requested_ms
        SettleActualMs = if ($null -eq $result.settle_elapsed_us) { $null } else { [Math]::Round(([double]$result.settle_elapsed_us / 1000), 3) }
        CaptureMs = $observation.total_capture_ms
        ResponseWriteMs = if ($null -eq $response.data.response_serialize_write_us) { $null } else { [Math]::Round(([double]$response.data.response_serialize_write_us / 1000), 3) }
        ResponseCharacters = $response.data.response_characters
        ContextImagesUpperBound = $observation.context.cumulative_images_returned
        ContextPatchesUpperBound = $observation.context.cumulative_estimated32_pixel_patches
    })
}

$gapValues = @($calls | Where-Object { $null -ne $_.LlmOrchestrationGapMs } | ForEach-Object { [double]$_.LlmOrchestrationGapMs })
$summary = [pscustomobject]@{
    SessionId = $SessionId
    ToolCalls = $calls.Count
    AtomicActions = $actions.Count
    TotalDriverMs = [Math]::Round((($calls | Measure-Object DriverMs -Sum).Sum), 3)
    TotalLlmOrchestrationGapMs = [Math]::Round((($gapValues | Measure-Object -Sum).Sum), 3)
    MeanLlmOrchestrationGapMs = if ($gapValues.Count -eq 0) { $null } else { [Math]::Round((($gapValues | Measure-Object -Average).Average), 3) }
    MaximumContextImagesUpperBound = ($calls | Measure-Object ContextImagesUpperBound -Maximum).Maximum
    MaximumContextPatchesUpperBound = ($calls | Measure-Object ContextPatchesUpperBound -Maximum).Maximum
    CacheMetrics = 'Low-level Codex-route cache counters require -CodexRolloutPath; high-level AgentRun counters come directly from its provider response.'
}

$agentProfile = $null
if ([string]::IsNullOrWhiteSpace($AgentRunId)) {
    $AgentRunId = [string](($session | Where-Object { $_.event -eq 'agent.run_started' } | Select-Object -Last 1).operation_id)
}
if (-not [string]::IsNullOrWhiteSpace($AgentRunId)) {
    $agentEvents = @($session | Where-Object { $_.operation_id -eq $AgentRunId -and $_.event -like 'agent.*' })
    $agentCompleted = $agentEvents | Where-Object { $_.event -eq 'agent.run_completed' } | Select-Object -Last 1
    $providerEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.provider_completed' })
    $policyEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.policy_evaluated' })
    $completionGuardEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.completion_guard_evaluated' })
    $routeEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.decision_routed' })
    $iterationEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.iteration_completed' })
    $observationEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.observation_captured' })
    $knowledgeEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.knowledge_retrieved' })
    $runbookStepEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.runbook_step_executed' })
    $runbookUncertainEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.runbook_step_effect_uncertain' })
    $runbookFailedEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.runbook_step_attempt_failed' })
    $routeLearningEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.route_learning_completed' })
    $routeLearningFailedEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.route_learning_unavailable' })
    $runbookAttemptEvents = @($runbookStepEvents) + @($runbookUncertainEvents) + @($runbookFailedEvents)
    $runbookDispatchedEvents = @($runbookStepEvents) + @($runbookUncertainEvents)
    $launchEvent = $agentEvents | Where-Object { $_.event -eq 'agent.launch_completed' } | Select-Object -Last 1
    $recoveryEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.recovery' })
    if ($null -ne $agentCompleted) {
        $activeElapsedMs = [double]$agentCompleted.data.elapsed_ms
        $decisionMs = [double](($providerEvents | ForEach-Object { [double]$_.data.timings_us.decision_complete_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $requestBuildMs = [double](($providerEvents | ForEach-Object { [double]$_.data.timings_us.request_build_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $parseMs = [double](($providerEvents | ForEach-Object { [double]$_.data.parse_us / 1000 } | Measure-Object -Sum).Sum)
        $policyMs = [double](($policyEvents | ForEach-Object { [double]$_.data.elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $completionGuardMs = [double](($completionGuardEvents | ForEach-Object { [double]$_.data.elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $imageStageMs = [double](($providerEvents | ForEach-Object { [double]$_.data.local_timings_us.image_stage_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $connectionAcquireMs = [double](($providerEvents | ForEach-Object { [double]$_.data.local_timings_us.connection_acquire_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $sessionSetupMs = [double](($providerEvents | ForEach-Object { [double]$_.data.local_timings_us.session_setup_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $payloadBuildMs = [double](($providerEvents | ForEach-Object { [double]$_.data.local_timings_us.payload_build_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $preparationMs = [double](($routeEvents | ForEach-Object { [double]$_.data.preparation_us / 1000 } | Measure-Object -Sum).Sum)
        $providerWallMs = [double](($routeEvents | ForEach-Object { [double]$_.data.provider_wall_us / 1000 } | Measure-Object -Sum).Sum)
        $routeMs = [double](($routeEvents | ForEach-Object { [double]$_.data.route_us / 1000 } | Measure-Object -Sum).Sum)
        $iterationValues = @($routeEvents | ForEach-Object { [double]$_.data.iteration_us / 1000 })
        $firstEventValues = @($providerEvents | ForEach-Object { [double]$_.data.timings_us.first_event_microseconds / 1000 })
        $firstDecisionValues = @($providerEvents | ForEach-Object { [double]$_.data.timings_us.first_decision_delta_microseconds / 1000 })
        $outputFillValues = @($providerEvents | ForEach-Object {
            $firstDecisionUs = [double]$_.data.timings_us.first_decision_delta_microseconds
            $decisionCompleteUs = [double]$_.data.timings_us.decision_complete_microseconds
            if ($firstDecisionUs -ge 0 -and $decisionCompleteUs -ge $firstDecisionUs) {
                ($decisionCompleteUs - $firstDecisionUs) / 1000
            }
        })
        $actionMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.action_execution_us / 1000 } | Measure-Object -Sum).Sum)
        $pointerPacingMs = [double](($iterationEvents | ForEach-Object {
            $_.data.actions | ForEach-Object { [double]$_.pointer_pacing_milliseconds }
        } | Measure-Object -Sum).Sum)
        $settleMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.settle_elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $captureMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.capture_total_us / 1000 } | Measure-Object -Sum).Sum)
        $knowledgeRetrievalMs = [double](($knowledgeEvents | ForEach-Object { [double]$_.data.elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $runbookStepMs = [double](($runbookAttemptEvents | ForEach-Object { [double]$_.data.total_us / 1000 } | Measure-Object -Sum).Sum)
        $runbookDispatchMs = [double](($runbookDispatchedEvents | ForEach-Object { [double]$_.data.dispatch_us / 1000 } | Measure-Object -Sum).Sum)
        $runbookReadinessMs = [double](($runbookStepEvents | ForEach-Object { [double]$_.data.readiness_us / 1000 } | Measure-Object -Sum).Sum)
        $routeLearningMs = [double](($routeLearningEvents | ForEach-Object { [double]$_.data.elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $runbookKindCounts = @($runbookAttemptEvents | Group-Object {
            if ($null -ne $_.data.PSObject.Properties['step_kind'] -and -not [string]::IsNullOrWhiteSpace([string]$_.data.step_kind)) {
                [string]$_.data.step_kind
            }
            else {
                'legacy_unspecified'
            }
        } | ForEach-Object { [pscustomobject]@{ Kind = $_.Name; Count = $_.Count } })
        $inputTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.input_tokens } | Measure-Object -Sum).Sum)
        $cachedTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.cached_input_tokens } | Measure-Object -Sum).Sum)
        $outputTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.output_tokens } | Measure-Object -Sum).Sum)
        $reasoningTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.reasoning_tokens } | Measure-Object -Sum).Sum)
        $imageBytes = [long](($providerEvents | ForEach-Object { [long]$_.data.image_bytes } | Measure-Object -Sum).Sum)
        $actionsExecuted = [int]$agentCompleted.data.actions_executed
        $initialObservation = $observationEvents |
            Where-Object { $_.data.phase -eq 'initial' } |
            Select-Object -First 1
        $initialCaptureStages = if ($null -eq $initialObservation) { @() } else {
            @($initialObservation.data.displays | ForEach-Object { $_.capture_timings_us })
        }
        $agentProfile = [pscustomobject]@{
            RunId = $AgentRunId
            Status = [string]$agentCompleted.data.status
            Provider = [string](($agentEvents | Where-Object { $_.event -eq 'agent.run_started' } | Select-Object -First 1).data.provider)
            Model = [string](($agentEvents | Where-Object { $_.event -eq 'agent.run_started' } | Select-Object -First 1).data.model)
            ModelTurns = [int]$agentCompleted.data.model_turns
            ActionsExecuted = $actionsExecuted
            ActiveElapsedMs = [Math]::Round($activeElapsedMs, 3)
            DisplayCount = if ($null -eq $initialObservation) { $null } else { [int]$initialObservation.data.display_count }
            CaptureScope = if ($null -eq $initialObservation) { $null } else { [string]$initialObservation.data.capture_scope }
            DisplayTopology = if ($null -eq $initialObservation) { @() } else { @($initialObservation.data.displays) }
            InitialCaptureMs = if ($null -eq $initialObservation) { $null } else {
                [Math]::Round([double]$initialObservation.data.capture_total_us / 1000, 3)
            }
            InitialCaptureSurfaceSetupMs = [Math]::Round([double](($initialCaptureStages | ForEach-Object { [double]$_.surface_setup_microseconds / 1000 } | Measure-Object -Sum).Sum), 3)
            InitialCaptureBlitMs = [Math]::Round([double](($initialCaptureStages | ForEach-Object { [double]$_.blit_microseconds / 1000 } | Measure-Object -Sum).Sum), 3)
            InitialCaptureMaterializeMs = [Math]::Round([double](($initialCaptureStages | ForEach-Object { [double]$_.materialize_microseconds / 1000 } | Measure-Object -Sum).Sum), 3)
            InitialCaptureResizeMs = [Math]::Round([double](($initialCaptureStages | ForEach-Object { [double]$_.resize_microseconds / 1000 } | Measure-Object -Sum).Sum), 3)
            InitialCaptureEncodeMs = [Math]::Round([double](($initialCaptureStages | ForEach-Object { [double]$_.encode_microseconds / 1000 } | Measure-Object -Sum).Sum), 3)
            InitialLaunchMs = if ($null -eq $launchEvent) { $null } else { [Math]::Round([double]$launchEvent.data.total_us / 1000, 3) }
            LaunchDispatchMs = if ($null -eq $launchEvent) { $null } else { [Math]::Round([double]$launchEvent.data.dispatch_us / 1000, 3) }
            LaunchReadinessWaitMs = if ($null -eq $launchEvent) { $null } else { [Math]::Round([double]$launchEvent.data.readiness_wait_us / 1000, 3) }
            LaunchTargetFound = if ($null -eq $launchEvent) { $null } else { [bool]$launchEvent.data.target_found }
            LaunchTargetActivated = if ($null -eq $launchEvent) { $null } else { [bool]$launchEvent.data.target_activated }
            LaunchForegroundProcess = if ($null -eq $launchEvent) { $null } else { [string]$launchEvent.data.foreground_process }
            ActionsPerSecond = if ($activeElapsedMs -le 0) { $null } else { [Math]::Round(1000 * $actionsExecuted / $activeElapsedMs, 3) }
            ActionsPerModelTurn = if ($providerEvents.Count -eq 0) { $null } else { [Math]::Round([double]$actionsExecuted / $providerEvents.Count, 3) }
            OuterMcpCallsAvoided = [Math]::Max(0, $providerEvents.Count - 1)
            TotalModelDecisionMs = [Math]::Round($decisionMs, 3)
            ModelDecisionPercent = if ($activeElapsedMs -le 0) { $null } else { [Math]::Round(100 * $decisionMs / $activeElapsedMs, 1) }
            TotalRequestBuildMs = [Math]::Round($requestBuildMs, 3)
            TotalDecisionParseMs = [Math]::Round($parseMs, 3)
            TotalPolicyEvaluationMs = [Math]::Round($policyMs, 3)
            CompletionGuardChecks = $completionGuardEvents.Count
            CompletionGuardMatches = @($completionGuardEvents | Where-Object { [bool]$_.data.matched }).Count
            TotalCompletionGuardMs = [Math]::Round($completionGuardMs, 3)
            TotalProviderImageStageMs = [Math]::Round($imageStageMs, 3)
            TotalProviderConnectionAcquireMs = [Math]::Round($connectionAcquireMs, 3)
            TotalProviderSessionSetupMs = [Math]::Round($sessionSetupMs, 3)
            TotalProviderPayloadBuildMs = [Math]::Round($payloadBuildMs, 3)
            TotalLoopPreparationMs = [Math]::Round($preparationMs, 3)
            TotalProviderWallMs = [Math]::Round($providerWallMs, 3)
            TotalDecisionRouteMs = [Math]::Round($routeMs, 3)
            P50IterationMs = if ($iterationValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-ProfilePercentile $iterationValues 0.50), 3)
            }
            P95IterationMs = if ($iterationValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-ProfilePercentile $iterationValues 0.95), 3)
            }
            MeanFirstEventMs = if ($firstEventValues.Count -eq 0) { $null } else { [Math]::Round(($firstEventValues | Measure-Object -Average).Average, 3) }
            MeanFirstDecisionDeltaMs = if ($firstDecisionValues.Count -eq 0) { $null } else { [Math]::Round(($firstDecisionValues | Measure-Object -Average).Average, 3) }
            MeanOutputFillMs = if ($outputFillValues.Count -eq 0) { $null } else { [Math]::Round(($outputFillValues | Measure-Object -Average).Average, 3) }
            P50OutputFillMs = if ($outputFillValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-ProfilePercentile $outputFillValues 0.50), 3)
            }
            P95OutputFillMs = if ($outputFillValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-ProfilePercentile $outputFillValues 0.95), 3)
            }
            TotalActionExecutionMs = [Math]::Round($actionMs, 3)
            TotalPointerPacingMs = [Math]::Round($pointerPacingMs, 3)
            TotalSettleMs = [Math]::Round($settleMs, 3)
            TotalCaptureMs = [Math]::Round($captureMs, 3)
            KnowledgeRetrievals = $knowledgeEvents.Count
            TotalKnowledgeRetrievalMs = [Math]::Round($knowledgeRetrievalMs, 3)
            MaximumRetrievedContextCharacters = ($knowledgeEvents | ForEach-Object { [int]$_.data.context_characters } | Measure-Object -Maximum).Maximum
            RunbookStepsExecuted = $runbookStepEvents.Count
            RunbookStepAttempts = $runbookAttemptEvents.Count
            RunbookStepFailures = $runbookFailedEvents.Count
            RunbookUncertainEffects = $runbookUncertainEvents.Count
            RunbookStepKinds = $runbookKindCounts
            TotalRunbookStepMs = [Math]::Round($runbookStepMs, 3)
            TotalRunbookDispatchMs = [Math]::Round($runbookDispatchMs, 3)
            TotalRunbookReadinessMs = [Math]::Round($runbookReadinessMs, 3)
            RunbookTargetsObserved = @($runbookStepEvents | Where-Object { [bool]$_.data.target_observed }).Count
            RunbookExistingTargetsReused = @($runbookStepEvents | Where-Object { [bool]$_.data.reused_existing_target }).Count
            RouteLearningSamples = [int](($routeLearningEvents | ForEach-Object { [int]$_.data.sample_count } | Measure-Object -Sum).Sum)
            TotalRouteLearningMs = [Math]::Round($routeLearningMs, 3)
            RouteLearningFailures = $routeLearningFailedEvents.Count
            InputTokens = $inputTokens
            CachedInputTokens = $cachedTokens
            CachePercent = if ($inputTokens -le 0) { $null } else { [Math]::Round(100 * [double]$cachedTokens / $inputTokens, 1) }
            OutputTokens = $outputTokens
            ReasoningTokens = $reasoningTokens
            TotalImageBytes = $imageBytes
            MaximumNoProgressTurns = ($iterationEvents | ForEach-Object { [int]$_.data.consecutive_no_progress_turns } | Measure-Object -Maximum).Maximum
            Recoveries = $recoveryEvents.Count
            RecoveryCategories = @($recoveryEvents | Group-Object { [string]$_.data.category } | ForEach-Object {
                [pscustomobject]@{ Category = $_.Name; Count = $_.Count }
            })
        }
    }
}

$modelUsage = $null
if (-not [string]::IsNullOrWhiteSpace($CodexRolloutPath)) {
    if (-not (Test-Path -LiteralPath $CodexRolloutPath -PathType Leaf)) {
        throw "Codex rollout not found: $CodexRolloutPath"
    }

    $toolEvents = @($session | Where-Object { $_.event -in @('tool.started', 'mcp.response_written') })
    $windowStart = [DateTimeOffset]::Parse([string]($toolEvents | Select-Object -First 1).timestamp)
    $windowEnd = [DateTimeOffset]::Parse([string]($toolEvents | Select-Object -Last 1).timestamp)
    $usageRows = @(
        Get-Content -LiteralPath $CodexRolloutPath |
            ForEach-Object {
                try {
                    $entry = $_ | ConvertFrom-Json
                    if ($entry.type -ne 'event_msg' -or $entry.payload.type -ne 'token_count') {
                        return
                    }

                    $timestamp = [DateTimeOffset]::Parse([string]$entry.timestamp)
                    $usage = $entry.payload.info.last_token_usage
                    if ($timestamp -lt $windowStart -or $timestamp -gt $windowEnd -or $null -eq $usage -or $usage.input_tokens -le 0) {
                        return
                    }

                    [pscustomobject]@{
                        Timestamp = $timestamp
                        InputTokens = [long]$usage.input_tokens
                        CachedInputTokens = [long]$usage.cached_input_tokens
                        UncachedInputTokens = [long]$usage.input_tokens - [long]$usage.cached_input_tokens
                        OutputTokens = [long]$usage.output_tokens
                        ReasoningOutputTokens = [long]$usage.reasoning_output_tokens
                    }
                }
                catch {
                    # Ignore unrelated or partially written rollout lines.
                }
            }
    )

    if ($usageRows.Count -gt 0) {
        $totalInput = [long](($usageRows | Measure-Object InputTokens -Sum).Sum)
        $totalCached = [long](($usageRows | Measure-Object CachedInputTokens -Sum).Sum)
        $modelUsage = [pscustomobject]@{
            Turns = $usageRows.Count
            FirstInputTokens = $usageRows[0].InputTokens
            LastInputTokens = $usageRows[-1].InputTokens
            ActiveInputGrowthTokens = $usageRows[-1].InputTokens - $usageRows[0].InputTokens
            TotalInputTokens = $totalInput
            TotalCachedInputTokens = $totalCached
            TotalUncachedInputTokens = $totalInput - $totalCached
            OverallCachePercent = if ($totalInput -eq 0) { $null } else { [Math]::Round(100 * [double]$totalCached / $totalInput, 1) }
            LastTurnUncachedInputTokens = $usageRows[-1].UncachedInputTokens
            Source = $CodexRolloutPath
            Privacy = 'Only numeric token_count events were read; prompt, image, and message content were not emitted.'
        }
    }
}

$profile = [pscustomobject]@{
    Summary = $summary
    AgentRun = $agentProfile
    ModelUsage = $modelUsage
    Calls = @($calls)
    Actions = @($actions)
}

if ($Json) {
    $profile | ConvertTo-Json -Depth 8
    return
}

$summary | Format-List
if ($null -ne $agentProfile) {
    $agentProfile | Format-List
}
if ($null -ne $modelUsage) {
    $modelUsage | Format-List
}
$calls | Sort-Object Sequence | Format-Table -AutoSize
if ($actions.Count -gt 0) {
    $actions | Sort-Object Sequence, Action | Format-Table -AutoSize
}
