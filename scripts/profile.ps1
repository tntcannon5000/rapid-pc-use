[CmdletBinding()]
param(
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'RapidPcUse\rapid-pc-use.log'),
    [string]$SessionId,
    [string]$AgentRunId,
    [string]$CodexRolloutPath,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

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
    $iterationEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.iteration_completed' })
    $recoveryEvents = @($agentEvents | Where-Object { $_.event -eq 'agent.recovery' })
    if ($null -ne $agentCompleted) {
        $activeElapsedMs = [double]$agentCompleted.data.elapsed_ms
        $decisionMs = [double](($providerEvents | ForEach-Object { [double]$_.data.timings_us.decision_complete_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $requestBuildMs = [double](($providerEvents | ForEach-Object { [double]$_.data.timings_us.request_build_microseconds / 1000 } | Measure-Object -Sum).Sum)
        $firstEventValues = @($providerEvents | ForEach-Object { [double]$_.data.timings_us.first_event_microseconds / 1000 })
        $firstDecisionValues = @($providerEvents | ForEach-Object { [double]$_.data.timings_us.first_decision_delta_microseconds / 1000 })
        $actionMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.action_execution_us / 1000 } | Measure-Object -Sum).Sum)
        $settleMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.settle_elapsed_us / 1000 } | Measure-Object -Sum).Sum)
        $captureMs = [double](($iterationEvents | ForEach-Object { [double]$_.data.capture_total_us / 1000 } | Measure-Object -Sum).Sum)
        $inputTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.input_tokens } | Measure-Object -Sum).Sum)
        $cachedTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.cached_input_tokens } | Measure-Object -Sum).Sum)
        $outputTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.output_tokens } | Measure-Object -Sum).Sum)
        $reasoningTokens = [long](($providerEvents | ForEach-Object { [long]$_.data.usage.reasoning_tokens } | Measure-Object -Sum).Sum)
        $imageBytes = [long](($providerEvents | ForEach-Object { [long]$_.data.image_bytes } | Measure-Object -Sum).Sum)
        $actionsExecuted = [int]$agentCompleted.data.actions_executed
        $agentProfile = [pscustomobject]@{
            RunId = $AgentRunId
            Status = [string]$agentCompleted.data.status
            Provider = [string](($agentEvents | Where-Object { $_.event -eq 'agent.run_started' } | Select-Object -First 1).data.provider)
            Model = [string](($agentEvents | Where-Object { $_.event -eq 'agent.run_started' } | Select-Object -First 1).data.model)
            ModelTurns = [int]$agentCompleted.data.model_turns
            ActionsExecuted = $actionsExecuted
            ActiveElapsedMs = [Math]::Round($activeElapsedMs, 3)
            ActionsPerSecond = if ($activeElapsedMs -le 0) { $null } else { [Math]::Round(1000 * $actionsExecuted / $activeElapsedMs, 3) }
            ActionsPerModelTurn = if ($providerEvents.Count -eq 0) { $null } else { [Math]::Round([double]$actionsExecuted / $providerEvents.Count, 3) }
            OuterMcpCallsAvoided = [Math]::Max(0, $providerEvents.Count - 1)
            TotalModelDecisionMs = [Math]::Round($decisionMs, 3)
            ModelDecisionPercent = if ($activeElapsedMs -le 0) { $null } else { [Math]::Round(100 * $decisionMs / $activeElapsedMs, 1) }
            TotalRequestBuildMs = [Math]::Round($requestBuildMs, 3)
            MeanFirstEventMs = if ($firstEventValues.Count -eq 0) { $null } else { [Math]::Round(($firstEventValues | Measure-Object -Average).Average, 3) }
            MeanFirstDecisionDeltaMs = if ($firstDecisionValues.Count -eq 0) { $null } else { [Math]::Round(($firstDecisionValues | Measure-Object -Average).Average, 3) }
            TotalActionExecutionMs = [Math]::Round($actionMs, 3)
            TotalSettleMs = [Math]::Round($settleMs, 3)
            TotalCaptureMs = [Math]::Round($captureMs, 3)
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
