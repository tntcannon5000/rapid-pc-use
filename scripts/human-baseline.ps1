[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$RunHuman,
    [ValidateSet('click-ladder-v1', 'form-tab-v1')]
    [string]$FixtureId = 'form-tab-v1',
    [ValidateRange(1, 100)]
    [int]$Repetitions = 5,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureScript = Join-Path $PSScriptRoot 'performance-fixture.ps1'
$statePath = Join-Path $env:LOCALAPPDATA 'RapidPcUse\PerformanceFixture\state.json'
. (Join-Path $PSScriptRoot 'benchmark-support.ps1')

if (-not $RunHuman) {
    throw 'The skilled-human benchmark requires the explicit -RunHuman switch.'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputPath = Join-Path $root "benchmark-results\human-$FixtureId-$timestamp.json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path $OutputPath -Parent)) | Out-Null

$rows = [Collections.Generic.List[object]]::new()
for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
    [void](Read-Host "Run $repetition/${Repetitions}: press Enter; start immediately when the fixture appears")
    $launchWall = [Diagnostics.Stopwatch]::StartNew()
    & $fixtureScript -Phase Reset -FixtureId $FixtureId -RunNumber $repetition -Repetition $repetition `
        -Model 'skilled-human' -Reasoning 'none' -CaptureTier '900'
    $readyWallMs = $launchWall.Elapsed.TotalMilliseconds
    $runToken = "bench-$FixtureId-$repetition"
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $state = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            try {
                $candidate = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
                if ($candidate.runToken -eq $runToken -and $candidate.status -eq 'completed') {
                    $state = $candidate
                    break
                }
            }
            catch {
                # Retry if the fixture is between its atomic temporary write and move.
            }
        }
        Start-Sleep -Milliseconds 5
    }
    $completionWallMs = $launchWall.Elapsed.TotalMilliseconds
    $verificationJson = [string](& $fixtureScript -Phase Verify -FixtureId $FixtureId `
            -RunNumber $repetition -Repetition $repetition -Model 'skilled-human' `
            -Reasoning 'none' -CaptureTier '900')
    $verification = ConvertFrom-RapidPcFixtureVerification $verificationJson
    $rows.Add([pscustomobject]@{
            Repetition = $repetition
            FixtureSuccess = $verification.Success
            UsefulActions = $verification.UsefulActions
            ErrorActions = $verification.ErrorActions
            FixtureReadyWallMs = [Math]::Round($readyWallMs, 3)
            ReadyToCompleteWallMs = if ($null -eq $state) { $null } else {
                [Math]::Round($completionWallMs - $readyWallMs, 3)
            }
            FixtureActiveMs = $verification.FixtureElapsedMs
        })
}

$successful = @($rows | Where-Object FixtureSuccess)
$activeValues = @($successful | ForEach-Object { [double]$_.FixtureActiveMs })
$readyValues = @($successful | ForEach-Object { [double]$_.ReadyToCompleteWallMs })
$totalUsefulActions = [double](($successful | Measure-Object UsefulActions -Sum).Sum)
$totalActiveMs = [double](($successful | Measure-Object FixtureActiveMs -Sum).Sum)
$summary = [pscustomobject]@{
    Runs = $rows.Count
    SuccessfulRuns = $successful.Count
    SuccessRatePercent = [Math]::Round(100 * $successful.Count / $rows.Count, 1)
    SuccessfulUsefulActionsPerActiveMinute = if ($totalActiveMs -le 0) { $null } else {
        [Math]::Round(60000 * $totalUsefulActions / $totalActiveMs, 2)
    }
    P50FixtureActiveMs = if ($activeValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $activeValues 0.50), 3)
    }
    P95FixtureActiveMs = if ($activeValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $activeValues 0.95), 3)
    }
    P50ReadyToCompleteWallMs = if ($readyValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $readyValues 0.50), 3)
    }
}

$artifact = [ordered]@{
    SchemaVersion = 1
    CreatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Commit = [string](& git -C $root rev-parse HEAD)
    FixtureId = $FixtureId
    Repetitions = $Repetitions
    Summary = $summary
    Rows = @($rows)
}
$artifact | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8

[pscustomobject]@{
    Fixture = $FixtureId
    Runs = $summary.Runs
    SuccessRatePercent = $summary.SuccessRatePercent
    SuccessfulUsefulActionsPerActiveMinute = $summary.SuccessfulUsefulActionsPerActiveMinute
    P50FixtureActiveMs = $summary.P50FixtureActiveMs
    P95FixtureActiveMs = $summary.P95FixtureActiveMs
    Artifact = $OutputPath
}
