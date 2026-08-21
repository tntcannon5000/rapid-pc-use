[CmdletBinding()]
param(
    [Parameter(Mandatory)][switch]$RunLive,
    [switch]$AllowAccountMutations,
    [string]$ExecutablePath,
    [string]$ConfigPath,
    [ValidateSet('discord-dm', 'youtube', 'amazon-orders')]
    [string[]]$Scenarios = @('amazon-orders'),
    [string[]]$Models = @('gpt-5.6-luna', 'gpt-5.6-terra', 'gpt-5.6-sol'),
    [ValidateSet('none', 'low', 'medium', 'high')][string]$Reasoning = 'low',
    [ValidateSet('fast', 'flex')][string]$ServiceTier = 'fast',
    [ValidateSet('720', '900')][string]$CaptureTier = '900',
    [ValidateRange(1, 20)][int]$Repetitions = 1,
    [ValidateRange(10000, 300000)][int]$MaxDurationMs = 300000,
    [int]$RandomSeed = 20260821,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$Label = 'working-tree',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'real-world\scenario-catalog.ps1')
. (Join-Path $PSScriptRoot 'real-world\invoke-run.ps1')

function Get-RapidPcProfileValue {
    param([object]$Profile, [string]$Name)
    $property = $Profile.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-RapidPcPercentile {
    param([object[]]$Values, [double]$Percentile)
    if ($Values.Count -eq 0) { return $null }
    $ordered = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1)
    return [Math]::Round($ordered[$index], 3)
}

if (-not $RunLive) {
    throw 'This benchmark controls live applications. Pass -RunLive explicitly.'
}
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $root 'benchmark-config\local.json'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $name = 'real-world-{0}-{1}.json' -f $Label, [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputPath = Join-Path (Join-Path $root 'benchmark-results') $name
}

$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$config = Import-RapidPcRealWorldConfig -Path $ConfigPath
$profileScript = Join-Path $PSScriptRoot 'profile.ps1'

if ($Models.Count -eq 0 -or $Scenarios.Count -eq 0) {
    throw 'At least one model and scenario are required.'
}
foreach ($model in $Models) {
    if ($model -notmatch '^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$') {
        throw "Invalid model identifier '$model'."
    }
}
Assert-RapidPcBenchmarkSchedulePolicy `
    -Scenarios $Scenarios `
    -Models $Models `
    -Repetitions $Repetitions `
    -AllowAccountMutations ([bool]$AllowAccountMutations)

$random = [Random]::new($RandomSeed)
$schedule = [Collections.Generic.List[object]]::new()
for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
    $round = [Collections.Generic.List[object]]::new()
    foreach ($scenarioId in $Scenarios) {
        foreach ($model in $Models) {
            $round.Add([pscustomobject]@{ ScenarioId = $scenarioId; Model = $model; Repetition = $repetition })
        }
    }
    for ($index = $round.Count - 1; $index -gt 0; $index--) {
        $swap = $random.Next($index + 1)
        $temporary = $round[$index]
        $round[$index] = $round[$swap]
        $round[$swap] = $temporary
    }
    foreach ($item in $round) {
        $schedule.Add($item)
    }
}
$readOnlySchedule = @($schedule | Where-Object { $_.ScenarioId -eq 'amazon-orders' })
$mutationSchedule = @($schedule | Where-Object { $_.ScenarioId -ne 'amazon-orders' })
$schedule.Clear()
foreach ($item in @($readOnlySchedule) + @($mutationSchedule)) {
    $schedule.Add($item)
}

$rows = [Collections.Generic.List[object]]::new()
$runNumber = 0
$scheduleAborted = $false
$abortReason = $null
foreach ($configuration in $schedule) {
    $runNumber++
    $nonce = '{0:D2}-{1}' -f $runNumber, ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $scenario = New-RapidPcRealWorldScenario -Id $configuration.ScenarioId -Config $config -Nonce $nonce
    Write-Host ('Run {0}/{1}: {2}, {3}, repetition {4}' -f $runNumber, $schedule.Count, $scenario.Id, $configuration.Model, $configuration.Repetition)
    try {
        Reset-RapidPcRealWorldScenario -Scenario $scenario
        $run = Invoke-RapidPcRealWorldRun `
            -ExecutablePath $ExecutablePath `
            -Scenario $scenario `
            -Model $configuration.Model `
            -Reasoning $Reasoning `
            -ServiceTier $ServiceTier `
            -CaptureTier $CaptureTier `
            -MaxDurationMs $MaxDurationMs `
            -ProfileScript $profileScript
        $verification = Test-RapidPcRealWorldScenario `
            -Scenario $scenario `
            -Config $config `
            -Status $run.Status `
            -Summary $run.Summary

        $profile = $run.Profile
        $rows.Add([pscustomobject]@{
            RunNumber = $runNumber
            Repetition = [int]$configuration.Repetition
            Scenario = [string]$scenario.Id
            Model = [string]$configuration.Model
            Reasoning = $Reasoning
            ServiceTier = $ServiceTier
            CaptureTier = $CaptureTier
            Status = $run.Status
            Success = [bool]$verification.Success
            ProvisionalSuccess = [bool]$verification.ProvisionalSuccess
            VerificationLevel = [string]$verification.VerificationLevel
            StateIsolationVerified = [bool]$verification.StateIsolationVerified
            FailureCategory = $verification.FailureCategory
            CompletionMarkerSeen = [bool]$verification.CompletionMarkerSeen
            StartStateRestored = [bool]$verification.ApplicationClosed
            ObservedItemCount = $verification.ObservedItemCount
            ObservedReturnedCount = $verification.ObservedReturnedCount
            ReturnedCountBonus = [bool]$verification.ReturnedCountBonus
            MeaningfulCheckpoints = [int]$verification.MeaningfulCheckpoints
            SessionId = $run.SessionId
            ModelTurns = $run.ModelTurns
            ActionsExecuted = $run.ActionsExecuted
            AgentElapsedMs = $run.AgentElapsedMs
            PromptToResultWallMs = $run.OuterWallMs
            FirstEventMs = Get-RapidPcProfileValue $profile 'MeanFirstEventMs'
            FirstDecisionDeltaMs = Get-RapidPcProfileValue $profile 'MeanFirstDecisionDeltaMs'
            OutputFillMs = Get-RapidPcProfileValue $profile 'MeanOutputFillMs'
            TotalModelDecisionMs = Get-RapidPcProfileValue $profile 'TotalModelDecisionMs'
            TotalActionExecutionMs = Get-RapidPcProfileValue $profile 'TotalActionExecutionMs'
            TotalPointerPacingMs = Get-RapidPcProfileValue $profile 'TotalPointerPacingMs'
            TotalCaptureMs = Get-RapidPcProfileValue $profile 'TotalCaptureMs'
            TotalSettleMs = Get-RapidPcProfileValue $profile 'TotalSettleMs'
            TotalRequestBuildMs = Get-RapidPcProfileValue $profile 'TotalRequestBuildMs'
            TotalDecisionParseMs = Get-RapidPcProfileValue $profile 'TotalDecisionParseMs'
            InputTokens = Get-RapidPcProfileValue $profile 'InputTokens'
            OutputTokens = Get-RapidPcProfileValue $profile 'OutputTokens'
            HandoffCount = $run.HandoffCount
            HandoffReasons = @($run.HandoffReasons)
            RemoteContentScopeSupported = [bool]$run.SupportsRemoteContentScope
        })
        if (-not $verification.StateIsolationVerified) {
            $scheduleAborted = $true
            $abortReason = "The $($scenario.Id) adapter cannot independently verify exact account-state restoration. The schedule stopped before another model could inherit state from this run."
            Write-Warning $abortReason
            break
        }
    }
    catch {
        if ($null -ne $_.Exception.Data['RapidPcBenchmarkTerminal']) {
            throw
        }
        $rows.Add([pscustomobject]@{
            RunNumber = $runNumber
            Repetition = [int]$configuration.Repetition
            Scenario = [string]$scenario.Id
            Model = [string]$configuration.Model
            Reasoning = $Reasoning
            ServiceTier = $ServiceTier
            CaptureTier = $CaptureTier
            Status = 'infrastructure_failure'
            Success = $false
            ProvisionalSuccess = $false
            VerificationLevel = 'not_verified'
            StateIsolationVerified = -not [bool]$scenario.MutatesAccountState
            FailureCategory = 'infrastructure_failure'
            CompletionMarkerSeen = $false
            StartStateRestored = $false
            ObservedItemCount = $null
            ObservedReturnedCount = $null
            ReturnedCountBonus = $false
            MeaningfulCheckpoints = [int]$scenario.MeaningfulCheckpoints
            SessionId = $null
            ModelTurns = $null
            ActionsExecuted = $null
            AgentElapsedMs = $null
            PromptToResultWallMs = $null
            FirstEventMs = $null
            FirstDecisionDeltaMs = $null
            OutputFillMs = $null
            TotalModelDecisionMs = $null
            TotalActionExecutionMs = $null
            TotalPointerPacingMs = $null
            TotalCaptureMs = $null
            TotalSettleMs = $null
            TotalRequestBuildMs = $null
            TotalDecisionParseMs = $null
            InputTokens = $null
            OutputTokens = $null
            HandoffCount = $null
            HandoffReasons = @()
            RemoteContentScopeSupported = $false
        })
        Write-Warning "Run $runNumber failed before a valid result was produced ($($_.Exception.GetType().Name))."
        if ($scenario.MutatesAccountState) {
            $scheduleAborted = $true
            $abortReason = "The $($scenario.Id) run ended without independently verified account-state restoration. The schedule stopped before another model could inherit state from this run."
            Write-Warning $abortReason
            break
        }
    }
}

$summary = @(
    $rows | Group-Object Scenario, Model | ForEach-Object {
        $group = @($_.Group)
        $successful = @($group | Where-Object { $_.Success })
        $provisional = @($group | Where-Object { $_.ProvisionalSuccess })
        [pscustomobject]@{
            Scenario = [string]$group[0].Scenario
            Model = [string]$group[0].Model
            Runs = $group.Count
            SuccessfulRuns = $successful.Count
            SuccessRatePercent = [Math]::Round(100 * $successful.Count / $group.Count, 1)
            ProvisionalRuns = $provisional.Count
            P50PromptToResultMs = Get-RapidPcPercentile -Values @($group.PromptToResultWallMs | Where-Object { $null -ne $_ }) -Percentile 0.5
            P50FirstEventMs = Get-RapidPcPercentile -Values @($group.FirstEventMs | Where-Object { $null -ne $_ }) -Percentile 0.5
            P50OutputFillMs = Get-RapidPcPercentile -Values @($group.OutputFillMs | Where-Object { $null -ne $_ }) -Percentile 0.5
            SuccessfulCheckpointsPerMinute = if ($successful.Count -eq 0) { $null } else {
                $checkpoints = [double](($successful | Measure-Object MeaningfulCheckpoints -Sum).Sum)
                $milliseconds = [double](($successful | Measure-Object PromptToResultWallMs -Sum).Sum)
                if ($milliseconds -le 0) { $null } else { [Math]::Round(60000 * $checkpoints / $milliseconds, 2) }
            }
        }
    }
)

$artifact = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Label = $Label
    ExecutableSha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Commit = ([string](& git -C $root rev-parse HEAD)).Trim()
    DirtyWorktree = @(& git -C $root status --porcelain).Count -gt 0
    Models = @($Models)
    Scenarios = @($Scenarios)
    Reasoning = $Reasoning
    ServiceTier = $ServiceTier
    CaptureTier = $CaptureTier
    Repetitions = $Repetitions
    RandomSeed = $RandomSeed
    ContextIsolation = 'A new driver process and ephemeral Codex provider thread are used for every run.'
    VerificationLimit = 'Discord deletion and YouTube state restoration are only model-attested until an independent verifier is added. The runner stops after either scenario so another model cannot inherit unverified account state.'
    ScheduleAborted = $scheduleAborted
    AbortReason = $abortReason
    Privacy = 'Tasks, contacts, server names, video queries, order page text, screenshots, raw summaries, and config contents are not stored. Aggregate Amazon observed item and returned-item counts are retained in this locally ignored artifact.'
    Rows = @($rows)
    Summary = $summary
}

Assert-RapidPcBenchmarkArtifactPrivacy -Artifact $artifact -Config $config
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[IO.File]::WriteAllText($OutputPath, ($artifact | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$rows | Select-Object RunNumber, Scenario, Model, Status, Success, ProvisionalSuccess, VerificationLevel, ModelTurns, ActionsExecuted, PromptToResultWallMs, FirstEventMs, OutputFillMs | Format-Table -AutoSize
$summary | Sort-Object Scenario, Model | Format-Table -AutoSize
Write-Host "Benchmark artifact: $OutputPath"
