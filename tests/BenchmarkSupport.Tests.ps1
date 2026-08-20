$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'scripts\benchmark-support.ps1')

function Assert-BenchmarkTest {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [scriptblock]$Action,
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        return
    }

    throw $Message
}

$schedule = @(New-RapidPcBenchmarkSchedule `
    -Models @('luna', 'terra') `
    -Reasoning @('none', 'low') `
    -CaptureTiers @('720', '900') `
    -Repetitions 3 `
    -RandomSeed 42)
$sameSchedule = @(New-RapidPcBenchmarkSchedule `
    -Models @('luna', 'terra') `
    -Reasoning @('none', 'low') `
    -CaptureTiers @('720', '900') `
    -Repetitions 3 `
    -RandomSeed 42)
Assert-BenchmarkTest ($schedule.Count -eq 24) 'The repeated benchmark schedule has the wrong run count.'
Assert-BenchmarkTest (@($schedule.RunNumber | Select-Object -Unique).Count -eq 24) 'Benchmark run numbers are not unique.'
Assert-BenchmarkTest (
    (($schedule | ForEach-Object { "$($_.Model)/$($_.Reasoning)/$($_.CaptureTier)" }) -join ',') -eq
    (($sameSchedule | ForEach-Object { "$($_.Model)/$($_.Reasoning)/$($_.CaptureTier)" }) -join ',')) `
    'The seeded benchmark order is not reproducible.'
foreach ($configuration in @($schedule | Group-Object Model, Reasoning, CaptureTier)) {
    Assert-BenchmarkTest ($configuration.Count -eq 3) 'A configuration is not represented once per repetition.'
}

$success = ConvertFrom-RapidPcFixtureVerification -Json '{"success":true,"usefulActions":7,"errorActions":1,"fixtureElapsedMs":950}'
Assert-BenchmarkTest ($success.Success -and $success.UsefulActions -eq 7) 'A valid fixture success result did not parse.'
Assert-BenchmarkTest ($success.ErrorActions -eq 1 -and $success.FixtureElapsedMs -eq 950) 'Optional fixture timing/error metrics did not parse.'
$failure = ConvertFrom-RapidPcFixtureVerification -Json '{"success":false,"usefulActions":2,"failureCategory":"wrong_state"}'
Assert-BenchmarkTest (-not $failure.Success -and $failure.FailureCategory -eq 'wrong_state') 'A valid fixture failure result did not parse.'
Assert-Throws { ConvertFrom-RapidPcFixtureVerification -Json '{"success":false,"usefulActions":0}' } 'A failure without a category was accepted.'
Assert-Throws { ConvertFrom-RapidPcFixtureVerification -Json '{"success":true,"usefulActions":-1}' } 'A negative useful-action count was accepted.'
Assert-Throws { ConvertFrom-RapidPcFixtureVerification -Json '{"success":true,"usefulActions":1,"failureCategory":"free form text"}' } 'Free-form fixture output was accepted.'

$runResult = ConvertFrom-RapidPcRunResult -Result ([pscustomobject]@{
    status = 'completed'; sessionId = 'run-0123456789ab'; modelTurns = 3; actionsExecuted = 9; elapsedMs = 2000
})
Assert-BenchmarkTest ($runResult.ModelTurns -eq 3 -and $runResult.ActionObjects -eq 9 -and $runResult.ElapsedMs -eq 2000) 'The camel-case pc_run contract did not parse.'
Assert-Throws {
    ConvertFrom-RapidPcRunResult -Result ([pscustomobject]@{
        status = 'completed'; session_id = 'run-0123456789ab'; model_turns = 3; actions_executed = 9; elapsed_ms = 2000
    })
} 'The obsolete snake-case benchmark contract was accepted.'

Assert-BenchmarkTest ((Get-RapidPcBenchmarkPercentile @(10, 20, 30, 40) 0.50) -eq 20) 'The p50 nearest-rank calculation is wrong.'
Assert-BenchmarkTest ((Get-RapidPcBenchmarkPercentile @(10, 20, 30, 40) 0.95) -eq 40) 'The p95 nearest-rank calculation is wrong.'

$rows = @(
    [pscustomobject]@{
        Model = 'luna'; Reasoning = 'none'; CaptureTier = '720'; ElapsedMs = 1000
        ModelTurns = 2; ActionObjects = 6; FixtureSuccess = $true; UsefulActions = 5
        TotalModelDecisionMs = 800; TotalCaptureMs = 40; TotalSettleMs = 50; TotalActionExecutionMs = 10
    },
    [pscustomobject]@{
        Model = 'luna'; Reasoning = 'none'; CaptureTier = '720'; ElapsedMs = 2000
        ModelTurns = 3; ActionObjects = 9; FixtureSuccess = $false; UsefulActions = 1
        TotalModelDecisionMs = 1700; TotalCaptureMs = 70; TotalSettleMs = 80; TotalActionExecutionMs = 15
    }
)
$summary = @(New-RapidPcBenchmarkSummary -Rows $rows)[0]
Assert-BenchmarkTest ($summary.Runs -eq 2) 'The benchmark summary lost runs.'
Assert-BenchmarkTest ($summary.SuccessRatePercent -eq 50) 'The verified fixture success rate is wrong.'
Assert-BenchmarkTest ($summary.SuccessfulActionsPerMinute -eq 120) 'Successful APM is not based on useful fixture actions and total verified time.'
Assert-BenchmarkTest ($summary.ActionObjectsPerModelTurn -eq 3) 'Action objects per model turn is wrong.'
Assert-BenchmarkTest ($summary.P50ElapsedMs -eq 1000 -and $summary.P95ElapsedMs -eq 2000) 'Elapsed percentiles are wrong.'

$temporaryLog = Join-Path ([IO.Path]::GetTempPath()) ("rapid-pc-profile-test-{0}.jsonl" -f [Guid]::NewGuid().ToString('N'))
try {
    $profileEntries = @(
        [pscustomobject]@{
            timestamp = '2026-01-01T00:00:00Z'; session_id = 'target-session'; operation_id = 'run-target'; event = 'agent.run_started'
            data = [pscustomobject]@{ provider = 'fixture'; model = 'fixture-model' }
        },
        [pscustomobject]@{
            timestamp = '2026-01-01T00:00:01Z'; session_id = 'target-session'; operation_id = 'run-target'; event = 'agent.observation_captured'
            data = [pscustomobject]@{
                phase = 'initial'; display_count = 2; capture_total_us = 33000
                displays = @(
                    [pscustomobject]@{ display_id = 'display-0'; native_width = 1920; native_height = 1080 },
                    [pscustomobject]@{ display_id = 'display-1'; native_width = 2560; native_height = 1440 }
                )
            }
        },
        [pscustomobject]@{
            timestamp = '2026-01-01T00:00:02Z'; session_id = 'target-session'; operation_id = 'run-target'; event = 'agent.provider_completed'
            data = [pscustomobject]@{
                timings_us = [pscustomobject]@{
                    decision_complete_microseconds = 800000; request_build_microseconds = 10000
                    first_event_microseconds = 500000; first_decision_delta_microseconds = 700000
                }
                usage = [pscustomobject]@{ input_tokens = 100; cached_input_tokens = 80; output_tokens = 10; reasoning_tokens = 2 }
                image_bytes = 1234
            }
        },
        [pscustomobject]@{
            timestamp = '2026-01-01T00:00:03Z'; session_id = 'target-session'; operation_id = 'run-target'; event = 'agent.run_completed'
            data = [pscustomobject]@{ status = 'completed'; model_turns = 1; actions_executed = 3; elapsed_ms = 1000 }
        },
        [pscustomobject]@{
            timestamp = '2026-01-01T00:00:04Z'; session_id = 'newer-unrelated-session'; operation_id = 'run-other'; event = 'agent.run_started'
            data = [pscustomobject]@{ provider = 'fixture'; model = 'other' }
        }
    )
    [IO.File]::WriteAllLines(
        $temporaryLog,
        @($profileEntries | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 8 }),
        [Text.UTF8Encoding]::new($false))
    $profileJson = @(& (Join-Path $root 'scripts\profile.ps1') -LogPath $temporaryLog -AgentRunId 'run-target' -Json) -join [Environment]::NewLine
    $profile = $profileJson | ConvertFrom-Json
    Assert-BenchmarkTest ($profile.Summary.SessionId -eq 'target-session') 'AgentRunId did not select its owning driver session.'
    Assert-BenchmarkTest ($profile.AgentRun.DisplayCount -eq 2) 'The profiler lost initial display-topology telemetry.'
    Assert-BenchmarkTest ($profile.AgentRun.InitialCaptureMs -eq 33) 'The profiler reported the wrong initial capture wall time.'
}
finally {
    [IO.File]::Delete($temporaryLog)
}

Write-Host 'PASS benchmark scheduling, fixture verification, successful APM, and percentile aggregation'
