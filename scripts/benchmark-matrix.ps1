[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateLength(1, 4000)]
    [string]$Task,
    [Parameter(Mandatory)]
    [switch]$RunLive,
    [string[]]$Models = @('gpt-5.6-luna', 'gpt-5.6-terra', 'gpt-5.6-sol'),
    [ValidateSet('none', 'low', 'medium', 'high')]
    [string[]]$Reasoning = @('none', 'low'),
    [ValidateSet('720', '900')]
    [string[]]$CaptureTiers = @('720', '900'),
    [ValidateSet('fast', 'flex')]
    [string]$ServiceTier = 'fast',
    [ValidateRange(1, 100)]
    [int]$Repetitions = 1,
    [ValidateRange(0, 15000)]
    [int]$WarmupMilliseconds = 250,
    [ValidateRange(10000, 300000)]
    [int]$MaxDurationMs = 120000,
    [int]$RandomSeed = 1729,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$MachineId = 'local',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$TaskCategory = 'unspecified',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')]
    [string]$FixtureId = 'unverified',
    [string]$FixtureScript,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'
$profileScript = Join-Path $PSScriptRoot 'profile.ps1'
. (Join-Path $PSScriptRoot 'benchmark-support.ps1')

if (-not $RunLive) {
    throw 'Live benchmarking requires the explicit -RunLive switch.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the plugin before benchmarking: $executable"
}
if ($Models.Count -eq 0) {
    throw 'At least one model is required.'
}
foreach ($model in $Models) {
    if ($model -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$') {
        throw "Invalid benchmark model identifier: '$model'."
    }
}

$fixtureScriptPath = $null
if (-not [string]::IsNullOrWhiteSpace($FixtureScript)) {
    $fixtureScriptPath = [IO.Path]::GetFullPath($FixtureScript)
    if (-not (Test-Path -LiteralPath $fixtureScriptPath -PathType Leaf) -or
        [IO.Path]::GetExtension($fixtureScriptPath) -ne '.ps1') {
        throw 'FixtureScript must name an existing PowerShell script.'
    }
}
else {
    Write-Warning 'No fixture verifier was supplied. The run will report action-object throughput, but successful APM and success rate will remain null.'
}

if ($Repetitions -lt 20) {
    Write-Warning 'Fewer than 20 repetitions are exploratory and do not satisfy the performance release gate.'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $fileName = 'benchmark-{0}.json' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputPath = Join-Path (Join-Path $root 'benchmark-results') $fileName
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

function Invoke-FixtureHook {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Reset', 'Verify')]
        [string]$Phase,
        [Parameter(Mandatory)]
        [object]$Configuration
    )

    if ($null -eq $fixtureScriptPath) {
        return $null
    }

    $output = @(& $fixtureScriptPath `
        -Phase $Phase `
        -FixtureId $FixtureId `
        -RunNumber $Configuration.RunNumber `
        -Repetition $Configuration.Repetition `
        -Model $Configuration.Model `
        -Reasoning $Configuration.Reasoning `
        -CaptureTier $Configuration.CaptureTier)
    if ($Phase -eq 'Reset') {
        return $null
    }

    $json = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw 'The fixture verifier returned no JSON result.'
    }

    return ConvertFrom-RapidPcFixtureVerification -Json $json
}

function Read-McpResponse {
    param(
        [Parameter(Mandatory)]
        [IO.StreamReader]$Reader,
        [Parameter(Mandatory)]
        [string]$Operation
    )

    $line = $Reader.ReadLine()
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

$schedule = @(New-RapidPcBenchmarkSchedule `
    -Models $Models `
    -Reasoning $Reasoning `
    -CaptureTiers $CaptureTiers `
    -Repetitions $Repetitions `
    -RandomSeed $RandomSeed)
$rows = [Collections.Generic.List[object]]::new()

foreach ($configuration in $schedule) {
    Write-Host ('Run {0}/{1}: {2}, {3}, {4}p, repetition {5}' -f `
        $configuration.RunNumber,
        $schedule.Count,
        $configuration.Model,
        $configuration.Reasoning,
        $configuration.CaptureTier,
        $configuration.Repetition)
    $fixtureResetWall = [Diagnostics.Stopwatch]::StartNew()
    Invoke-FixtureHook -Phase Reset -Configuration $configuration
    $fixtureResetWall.Stop()

    $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['RAPID_PC_AGENT_ENABLED'] = '1'
    $startInfo.Environment['RAPID_PC_AGENT_PROVIDER'] = 'codex'
    $startInfo.Environment['RAPID_PC_AGENT_MODEL'] = $configuration.Model
    $startInfo.Environment['RAPID_PC_AGENT_REASONING'] = $configuration.Reasoning
    $startInfo.Environment['RAPID_PC_AGENT_SERVICE_TIER'] = $ServiceTier
    $startInfo.Environment['RAPID_PC_AGENT_IMAGE_DETAIL'] = 'original'
    $startInfo.Environment['RAPID_PC_CAPTURE_TIER'] = $configuration.CaptureTier

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $stderrTask = $null
    $stderr = ''
    $result = $null
    try {
        [void]$process.Start()
        $started = $true
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $initializeRequest = @{
            jsonrpc = '2.0'
            id = 1
            method = 'initialize'
            params = @{
                protocolVersion = '2025-11-25'
                capabilities = @{}
                clientInfo = @{ name = 'rapid-pc-use-benchmark'; version = '1' }
            }
        } | ConvertTo-Json -Compress -Depth 8
        $process.StandardInput.WriteLine($initializeRequest)
        $initializeResponse = Read-McpResponse -Reader $process.StandardOutput -Operation 'initialize'
        if ($null -ne $initializeResponse.error) {
            throw 'The benchmark driver rejected MCP initialization.'
        }

        if ($WarmupMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $WarmupMilliseconds
        }

        $request = @{
            jsonrpc = '2.0'
            id = 2
            method = 'tools/call'
            params = @{
                name = 'pc_run'
                arguments = @{
                    task = $Task
                    limits = @{ max_duration_ms = $MaxDurationMs }
                }
            }
        } | ConvertTo-Json -Compress -Depth 8
        $process.StandardInput.WriteLine($request)
        $response = Read-McpResponse -Reader $process.StandardOutput -Operation 'pc_run'
        if ($null -ne $response.error) {
            throw 'The benchmark driver returned an MCP error for pc_run.'
        }

        $result = $response.result.structuredContent
        $runResult = ConvertFrom-RapidPcRunResult -Result $result
    }
    finally {
        if ($started) {
            try { $process.StandardInput.Close() }
            catch { }
            if (-not $process.WaitForExit(5000)) {
                $process.Kill($true)
                [void]$process.WaitForExit(5000)
            }
            if ($null -ne $stderrTask) {
                $stderr = $stderrTask.GetAwaiter().GetResult()
            }
        }
        $exitCode = if ($started -and $process.HasExited) { $process.ExitCode } else { $null }
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        throw "The benchmark driver exited with code $exitCode. Stderr length: $($stderr.Length)."
    }

    $profileJson = @(& $profileScript -AgentRunId $runResult.AgentRunId -Json) -join [Environment]::NewLine
    $profile = $profileJson | ConvertFrom-Json
    if ($null -eq $profile.AgentRun -or $profile.AgentRun.RunId -ne $runResult.AgentRunId) {
        throw 'The benchmark could not join pc_run to its flushed telemetry profile.'
    }
    if ([string]$profile.AgentRun.Status -ne $runResult.Status -or
        [int]$profile.AgentRun.ModelTurns -ne $runResult.ModelTurns -or
        [int]$profile.AgentRun.ActionsExecuted -ne $runResult.ActionObjects -or
        [long]$profile.AgentRun.ActiveElapsedMs -ne $runResult.ElapsedMs) {
        throw 'The structured pc_run result and telemetry profile disagree.'
    }

    $fixtureVerifyWall = [Diagnostics.Stopwatch]::StartNew()
    $verification = Invoke-FixtureHook -Phase Verify -Configuration $configuration
    $fixtureVerifyWall.Stop()
    $elapsedMs = [long]$runResult.ElapsedMs
    $actionObjects = [int]$runResult.ActionObjects
    $modelTurns = [int]$runResult.ModelTurns
    $rows.Add([pscustomobject]@{
        RunNumber = [int]$configuration.RunNumber
        Repetition = [int]$configuration.Repetition
        Model = [string]$configuration.Model
        Reasoning = [string]$configuration.Reasoning
        ServiceTier = $ServiceTier
        CaptureTier = [string]$configuration.CaptureTier
        Status = [string]$runResult.Status
        FixtureSuccess = if ($null -eq $verification) { $null } else { [bool]$verification.Success }
        UsefulActions = if ($null -eq $verification) { $null } else { [long]$verification.UsefulActions }
        ErrorActions = if ($null -eq $verification) { $null } else { $verification.ErrorActions }
        FixtureElapsedMs = if ($null -eq $verification) { $null } else { $verification.FixtureElapsedMs }
        FixtureResetWallMs = if ($null -eq $fixtureScriptPath) { $null } else { [Math]::Round($fixtureResetWall.Elapsed.TotalMilliseconds, 3) }
        FixtureVerifyWallMs = if ($null -eq $fixtureScriptPath) { $null } else { [Math]::Round($fixtureVerifyWall.Elapsed.TotalMilliseconds, 3) }
        FailureCategory = if ($null -eq $verification) { $null } else { $verification.FailureCategory }
        ModelTurns = $modelTurns
        ActionObjects = $actionObjects
        ElapsedMs = $elapsedMs
        ActionObjectsPerSecond = if ($elapsedMs -le 0) { $null } else {
            [Math]::Round(1000 * [double]$actionObjects / $elapsedMs, 3)
        }
        ActionObjectsPerModelTurn = if ($modelTurns -le 0) { $null } else {
            [Math]::Round([double]$actionObjects / $modelTurns, 3)
        }
        DriverSessionId = [string]$profile.Summary.SessionId
        AgentRunId = [string]$runResult.AgentRunId
        Provider = [string]$profile.AgentRun.Provider
        DisplayCount = $profile.AgentRun.DisplayCount
        CaptureScope = $profile.AgentRun.CaptureScope
        DisplayTopology = @($profile.AgentRun.DisplayTopology)
        InitialCaptureMs = $profile.AgentRun.InitialCaptureMs
        InitialCaptureSurfaceSetupMs = $profile.AgentRun.InitialCaptureSurfaceSetupMs
        InitialCaptureBlitMs = $profile.AgentRun.InitialCaptureBlitMs
        InitialCaptureMaterializeMs = $profile.AgentRun.InitialCaptureMaterializeMs
        InitialCaptureResizeMs = $profile.AgentRun.InitialCaptureResizeMs
        InitialCaptureEncodeMs = $profile.AgentRun.InitialCaptureEncodeMs
        TotalModelDecisionMs = $profile.AgentRun.TotalModelDecisionMs
        TotalRequestBuildMs = $profile.AgentRun.TotalRequestBuildMs
        TotalDecisionParseMs = $profile.AgentRun.TotalDecisionParseMs
        TotalPolicyEvaluationMs = $profile.AgentRun.TotalPolicyEvaluationMs
        CompletionGuardChecks = $profile.AgentRun.CompletionGuardChecks
        CompletionGuardMatches = $profile.AgentRun.CompletionGuardMatches
        TotalCompletionGuardMs = $profile.AgentRun.TotalCompletionGuardMs
        TotalProviderImageStageMs = $profile.AgentRun.TotalProviderImageStageMs
        TotalProviderConnectionAcquireMs = $profile.AgentRun.TotalProviderConnectionAcquireMs
        TotalProviderSessionSetupMs = $profile.AgentRun.TotalProviderSessionSetupMs
        TotalProviderPayloadBuildMs = $profile.AgentRun.TotalProviderPayloadBuildMs
        TotalLoopPreparationMs = $profile.AgentRun.TotalLoopPreparationMs
        TotalProviderWallMs = $profile.AgentRun.TotalProviderWallMs
        TotalDecisionRouteMs = $profile.AgentRun.TotalDecisionRouteMs
        P50IterationMs = $profile.AgentRun.P50IterationMs
        P95IterationMs = $profile.AgentRun.P95IterationMs
        MeanFirstEventMs = $profile.AgentRun.MeanFirstEventMs
        MeanFirstDecisionDeltaMs = $profile.AgentRun.MeanFirstDecisionDeltaMs
        MeanOutputFillMs = $profile.AgentRun.MeanOutputFillMs
        P50OutputFillMs = $profile.AgentRun.P50OutputFillMs
        P95OutputFillMs = $profile.AgentRun.P95OutputFillMs
        TotalActionExecutionMs = $profile.AgentRun.TotalActionExecutionMs
        TotalSettleMs = $profile.AgentRun.TotalSettleMs
        TotalCaptureMs = $profile.AgentRun.TotalCaptureMs
        InputTokens = $profile.AgentRun.InputTokens
        CachedInputTokens = $profile.AgentRun.CachedInputTokens
        OutputTokens = $profile.AgentRun.OutputTokens
        ReasoningTokens = $profile.AgentRun.ReasoningTokens
        TotalImageBytes = $profile.AgentRun.TotalImageBytes
        MaximumNoProgressTurns = $profile.AgentRun.MaximumNoProgressTurns
        Recoveries = $profile.AgentRun.Recoveries
        RecoveryCategories = @($profile.AgentRun.RecoveryCategories)
    })
}

$summaries = @(New-RapidPcBenchmarkSummary -Rows @($rows))
$commit = [string](& git -C $root rev-parse HEAD)
$dirty = @(& git -C $root status --porcelain).Count -gt 0
$artifact = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Commit = $commit.Trim()
    DirtyWorktree = $dirty
    MachineId = $MachineId
    FixtureId = $FixtureId
    TaskCategory = $TaskCategory
    Route = 'pc_run/codex-session'
    ServiceTier = $ServiceTier
    ImageDetail = 'original'
    Repetitions = $Repetitions
    WarmupMilliseconds = $WarmupMilliseconds
    MaxDurationMilliseconds = $MaxDurationMs
    RandomSeed = $RandomSeed
    FixtureVerified = $null -ne $fixtureScriptPath
    Privacy = 'Task text, screenshots, typed content, model output, and fixture output are not stored.'
    Rows = @($rows)
    Summary = $summaries
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$json = $artifact | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))

$rows |
    Select-Object RunNumber, Repetition, Model, Reasoning, CaptureTier, Status, FixtureSuccess, UsefulActions, ModelTurns, ActionObjects, ElapsedMs |
    Format-Table -AutoSize
$summaries |
    Select-Object Model, Reasoning, CaptureTier, Runs, SuccessRatePercent, SuccessfulActionsPerMinute, ActionObjectsPerModelTurn, P50ElapsedMs, P95ElapsedMs |
    Sort-Object SuccessfulActionsPerMinute, P50ElapsedMs -Descending |
    Format-Table -AutoSize
Write-Host "Benchmark artifact: $OutputPath"
