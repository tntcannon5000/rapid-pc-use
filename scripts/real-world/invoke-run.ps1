Set-StrictMode -Version Latest

function Invoke-RapidPcRealWorldRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][object]$Scenario,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][ValidateSet('none', 'low', 'medium', 'high')][string]$Reasoning,
        [Parameter(Mandatory)][ValidateSet('fast', 'flex')][string]$ServiceTier,
        [Parameter(Mandatory)][ValidateSet('720', '900')][string]$CaptureTier,
        [Parameter(Mandatory)][ValidateRange(10000, 300000)][int]$MaxDurationMs,
        [Parameter(Mandatory)][string]$ProfileScript
    )

    $resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
    if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
        throw "Rapid PC Use executable not found: $resolvedExecutable"
    }

    $startInfo = New-RapidPcBenchmarkDriverStartInfo `
        -ExecutablePath $resolvedExecutable `
        -Model $Model `
        -Reasoning $Reasoning `
        -ServiceTier $ServiceTier `
        -CaptureTier $CaptureTier `
        -MaxDurationMs $MaxDurationMs

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $stderrTask = $null
    $responseResult = $null
    $pendingException = $null
    $handoffReasons = [Collections.Generic.List[string]]::new()
    $outerWall = [Diagnostics.Stopwatch]::new()
    try {
        [void]$process.Start()
        $started = $true
        $stderrTask = $process.StandardError.ReadToEndAsync()

        Send-RapidPcMcpRequest -Writer $process.StandardInput -Id 1 -Method 'initialize' -Params @{
            protocolVersion = '2025-11-25'
            capabilities = @{}
            clientInfo = @{ name = 'rapid-pc-use-real-world-benchmark'; version = '1' }
        }
        $initialize = Read-RapidPcMcpResponse -Reader $process.StandardOutput -Operation 'initialize' -TimeoutMilliseconds 30000
        if ($null -ne $initialize.PSObject.Properties['error']) {
            throw 'The benchmark driver rejected MCP initialization.'
        }

        Send-RapidPcMcpRequest -Writer $process.StandardInput -Id 2 -Method 'tools/list' -Params @{}
        $toolList = Read-RapidPcMcpResponse -Reader $process.StandardOutput -Operation 'tools/list' -TimeoutMilliseconds 30000
        if ($null -ne $toolList.PSObject.Properties['error']) {
            throw 'The benchmark driver rejected tools/list.'
        }
        $pcRun = @($toolList.result.tools | Where-Object { $_.name -eq 'pc_run' }) | Select-Object -First 1
        if ($null -eq $pcRun) {
            throw 'The selected executable does not advertise pc_run.'
        }
        $runProperties = $pcRun.inputSchema.properties
        $supportsDirectLaunch = $null -ne $runProperties.PSObject.Properties['launch_uri']
        $supportsExecutionContext = $null -ne $runProperties.PSObject.Properties['execution_context']

        $runArguments = @{
            task = [string]$Scenario.Task
            limits = @{
                max_model_turns = 50
                max_actions = 256
                max_duration_ms = $MaxDurationMs
                max_consecutive_no_progress_turns = 8
            }
            return_final_screenshot = $false
        }
        if ($supportsDirectLaunch -and -not [string]::IsNullOrWhiteSpace([string]$Scenario.LaunchUri)) {
            $runArguments.launch_uri = [string]$Scenario.LaunchUri
        }
        if ($supportsExecutionContext -and -not [string]::IsNullOrWhiteSpace([string]$Scenario.ExecutionContext)) {
            $runArguments.execution_context = [string]$Scenario.ExecutionContext
        }

        $outerWall.Restart()
        Send-RapidPcMcpRequest -Writer $process.StandardInput -Id 3 -Method 'tools/call' -Params @{
            name = 'pc_run'
            arguments = $runArguments
        }
        $response = Read-RapidPcMcpResponse -Reader $process.StandardOutput -Operation 'pc_run' -TimeoutMilliseconds ($MaxDurationMs + 15000)
        if ($null -ne $response.PSObject.Properties['error']) {
            throw 'The benchmark driver returned an MCP error for pc_run.'
        }
        Assert-RapidPcMcpResponseIsContinuable -Response $response

        $responseResult = $response.result.structuredContent
        $requestId = 3
        while ([string]$responseResult.status -eq 'needs_handoff') {
            if ($handoffReasons.Count -ge 3) {
                break
            }
            if ($null -eq $responseResult.handoff) {
                throw 'pc_run reported needs_handoff without a handoff payload.'
            }
            $reason = [string]$responseResult.handoff.reason
            $handoffReasons.Add($reason)
            $outerContext = Resolve-RapidPcBenchmarkHandoff `
                -Scenario $Scenario `
                -Reason $reason `
                -ActionsExecuted ([int]$responseResult.actionsExecuted)
            $requestId++
            Send-RapidPcMcpRequest -Writer $process.StandardInput -Id $requestId -Method 'tools/call' -Params @{
                name = 'pc_continue'
                arguments = @{
                    session_id = [string]$responseResult.sessionId
                    handoff_id = [string]$responseResult.handoff.handoffId
                    outer_context = $outerContext
                }
            }
            $continuation = Read-RapidPcMcpResponse -Reader $process.StandardOutput -Operation 'pc_continue' -TimeoutMilliseconds ($MaxDurationMs + 15000)
            if ($null -ne $continuation.PSObject.Properties['error']) {
                throw 'The benchmark driver returned an MCP error for pc_continue.'
            }
            Assert-RapidPcMcpResponseIsContinuable -Response $continuation
            $responseResult = $continuation.result.structuredContent
        }
        $outerWall.Stop()

        foreach ($required in @('status', 'sessionId', 'summary', 'modelTurns', 'actionsExecuted', 'elapsedMs')) {
            if ($null -eq $responseResult.PSObject.Properties[$required]) {
                throw "The final PC result omitted '$required'."
            }
        }
    }
    catch {
        $pendingException = $_.Exception
        throw
    }
    finally {
        if ($started) {
            try { $process.StandardInput.Close() } catch { }
            $stopped = Stop-RapidPcBenchmarkDriver -Process $process
            if (-not $stopped -and $null -eq $pendingException) {
                throw 'The benchmark driver could not be stopped after its MCP run.'
            }
        }
    }

    $stderr = if ($null -ne $stderrTask) { $stderrTask.GetAwaiter().GetResult() } else { '' }
    $exitCode = if ($started -and $process.HasExited) { $process.ExitCode } else { $null }
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw "The benchmark driver exited with code $exitCode. Stderr length: $($stderr.Length)."
    }

    # profile.ps1 predates strict-mode-safe access for heterogeneous log rows.
    # Invoke it with its historical language semantics, then restore this
    # runner's stricter boundary immediately.
    Set-StrictMode -Off
    try {
        $profileJson = @(& $ProfileScript -AgentRunId ([string]$responseResult.sessionId) -Json) -join [Environment]::NewLine
    }
    finally {
        Set-StrictMode -Version Latest
    }
    $profile = $profileJson | ConvertFrom-Json
    if ($null -eq $profile.AgentRun -or [string]$profile.AgentRun.RunId -ne [string]$responseResult.sessionId) {
        throw 'The benchmark could not join the run to its telemetry profile.'
    }

    return [pscustomobject]@{
        Status = [string]$responseResult.status
        SessionId = [string]$responseResult.sessionId
        Summary = [string]$responseResult.summary
        ModelTurns = [int]$responseResult.modelTurns
        ActionsExecuted = [int]$responseResult.actionsExecuted
        AgentElapsedMs = [long]$responseResult.elapsedMs
        OuterWallMs = [Math]::Round($outerWall.Elapsed.TotalMilliseconds, 3)
        SupportsRemoteContentScope = $supportsRemoteContentScope
        SupportsDirectLaunch = $supportsDirectLaunch
        SupportsExecutionContext = $supportsExecutionContext
        HandoffCount = $handoffReasons.Count
        HandoffReasons = @($handoffReasons)
        Profile = $profile.AgentRun
    }
}

function New-RapidPcBenchmarkDriverStartInfo {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][ValidateSet('none', 'low', 'medium', 'high')][string]$Reasoning,
        [Parameter(Mandatory)][ValidateSet('fast', 'flex')][string]$ServiceTier,
        [Parameter(Mandatory)][ValidateSet('720', '900')][string]$CaptureTier,
        [Parameter(Mandatory)][ValidateRange(10000, 300000)][int]$MaxDurationMs
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($ExecutablePath))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['RAPID_PC_AGENT_ENABLED'] = '1'
    $startInfo.Environment['RAPID_PC_AGENT_PROVIDER'] = 'codex'
    $startInfo.Environment['RAPID_PC_AGENT_MODEL'] = $Model
    $startInfo.Environment['RAPID_PC_AGENT_REASONING'] = $Reasoning
    $startInfo.Environment['RAPID_PC_AGENT_SERVICE_TIER'] = $ServiceTier
    $startInfo.Environment['RAPID_PC_AGENT_IMAGE_DETAIL'] = 'original'
    $startInfo.Environment['RAPID_PC_CAPTURE_TIER'] = $CaptureTier
    $startInfo.Environment['RAPID_PC_AGENT_MAX_DURATION_MS'] = [string]$MaxDurationMs
    return $startInfo
}

function Stop-RapidPcBenchmarkDriver {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [ValidateRange(0, 5000)][int]$GraceMilliseconds = 5000
    )

    try {
        if ($Process.HasExited -or $Process.WaitForExit($GraceMilliseconds)) { return $true }
    }
    catch { }

    try {
        $treeKill = @($Process.GetType().GetMethods() | Where-Object {
            $_.Name -eq 'Kill' -and $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType -eq [bool]
        }) | Select-Object -First 1
        if ($null -ne $treeKill) {
            [void]$treeKill.Invoke($Process, @($true))
        }
        else {
            $Process.Kill()
        }
    }
    catch {
        try { Stop-Process -Id $Process.Id -Force -ErrorAction Stop } catch { }
    }

    try { return $Process.WaitForExit(5000) } catch { return $false }
}

function Resolve-RapidPcBenchmarkHandoff {
    param(
        [Parameter(Mandatory)][object]$Scenario,
        [Parameter(Mandatory)][string]$Reason,
        [Parameter(Mandatory)][int]$ActionsExecuted
    )

    if ($Reason -eq 'need_terminal') {
        Start-Process ([string]$Scenario.LaunchUri)
        return "The scenario's fixed launch URI was opened. Continue from the fresh visible state; no additional authority was granted."
    }

    if ($Scenario.Id -eq 'discord-dm' -and $Reason -eq 'unsupported_capability') {
        return 'Complete every message, deletion, return-server, and full-exit milestone through the visible application. No command-line cleanup was performed because action count does not prove the earlier milestones; no additional authority was granted.'
    }

    if ($Scenario.Id -eq 'amazon-orders' -and $Reason -eq 'need_knowledge') {
        return 'Use the signed-in Orders interface and the date and item-quantity rule already present in the task. Report only the count you observe; no expected benchmark count is available and no additional authority was granted.'
    }

    if ($Reason -eq 'need_filesystem') {
        return 'This benchmark has no authorized filesystem dependency. Continue visually or report a genuine blocker; no additional authority was granted.'
    }

    return 'No additional verified fact is available. Continue from the fresh visible state or report a genuine blocker; no additional authority was granted.'
}

function Assert-RapidPcMcpResponseIsContinuable {
    param([Parameter(Mandatory)][object]$Response)

    $result = $Response.result
    $content = if ($null -eq $result.PSObject.Properties['content']) { @() } else { @($result.content) }
    $text = @($content | Where-Object { $_.type -eq 'text' } | ForEach-Object { [string]$_.text }) -join "`n"
    $isError = $null -ne $result.PSObject.Properties['isError'] -and $result.isError -eq $true
    $terminalKind = if ($text.IndexOf('USER_TAKEOVER', [StringComparison]::Ordinal) -ge 0) {
        'user_takeover'
    }
    elseif ($text.IndexOf('RAPID_PC_USE_FAILURE', [StringComparison]::Ordinal) -ge 0 -or $isError) {
        'driver_failure'
    }
    else {
        $null
    }

    if ($null -ne $terminalKind) {
        $exception = [InvalidOperationException]::new("The benchmark received terminal signal '$terminalKind' and must stop immediately.")
        $exception.Data['RapidPcBenchmarkTerminal'] = $terminalKind
        throw $exception
    }
}

function Send-RapidPcMcpRequest {
    param(
        [Parameter(Mandatory)][IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][int]$Id,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][hashtable]$Params
    )

    $json = @{
        jsonrpc = '2.0'
        id = $Id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Compress -Depth 12
    $Writer.WriteLine($json)
    $Writer.Flush()
}

function Read-RapidPcMcpResponse {
    param(
        [Parameter(Mandatory)][IO.StreamReader]$Reader,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][ValidateRange(1000, 315000)][int]$TimeoutMilliseconds
    )

    $readTask = $Reader.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMilliseconds)) {
        $exception = [TimeoutException]::new("The benchmark driver timed out while waiting for $Operation.")
        $exception.Data['RapidPcBenchmarkTerminal'] = 'driver_timeout'
        throw $exception
    }
    $line = $readTask.GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "The benchmark driver returned no MCP response for $Operation."
    }
    try {
        return $line | ConvertFrom-Json
    }
    catch {
        throw "The benchmark driver returned invalid JSON for $Operation."
    }
}
