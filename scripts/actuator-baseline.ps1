[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$RunLocal,
    [ValidateSet('click-ladder-v1', 'form-tab-v1')]
    [string]$FixtureId = 'form-tab-v1',
    [ValidateRange(1, 100)]
    [int]$Repetitions = 20,
    [ValidateRange(0, 10)]
    [int]$WarmupRuns = 1,
    [ValidateSet('720', '900')]
    [string]$CaptureTier = '900',
    [ValidateSet('full_desktop', 'active_window')]
    [string]$CaptureScope = 'full_desktop',
    [ValidateRange(0, 1000)]
    [int]$SettleMilliseconds = 35,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'
$fixtureScript = Join-Path $PSScriptRoot 'performance-fixture.ps1'
$statePath = Join-Path $env:LOCALAPPDATA 'RapidPcUse\PerformanceFixture\state.json'
$logPath = Join-Path $env:LOCALAPPDATA 'RapidPcUse\rapid-pc-use.log'
. (Join-Path $PSScriptRoot 'benchmark-support.ps1')

if (-not $RunLocal) {
    throw 'The deterministic actuator benchmark requires the explicit -RunLocal switch.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the plugin before benchmarking: $executable"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputPath = Join-Path $root "benchmark-results\actuator-$FixtureId-$timestamp.json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path $OutputPath -Parent)) | Out-Null

Add-Type -AssemblyName UIAutomationClient

function Invoke-Mcp {
    param(
        [Parameter(Mandatory)]
        [int]$Id,
        [Parameter(Mandatory)]
        [string]$Method,
        [Parameter(Mandatory)]
        [hashtable]$Parameters,
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process
    )

    $request = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters } |
        ConvertTo-Json -Depth 12 -Compress
    $Process.StandardInput.WriteLine($request)
    $line = $Process.StandardOutput.ReadLine()
    if (-not $line) {
        throw 'The driver closed its output before returning an MCP response.'
    }
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "MCP method '$Method' failed with JSON-RPC code $($response.error.code): $($response.error.message)"
    }
    if ($response.result.isError -eq $true) {
        $summary = [string](($response.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text)
        throw "MCP method '$Method' returned a terminal tool failure: $summary"
    }
    return $response
}

function Assert-McpActionCompleted([object]$Response, [string]$Phase) {
    if ($Response.result.structuredContent.status -eq 'action_interrupted') {
        $failureCode = [string]$Response.result.structuredContent.failure_code
        $nativeError = $Response.result.structuredContent.native_error_code
        $inBounds = $Response.result.structuredContent.target_within_virtual_desktop
        throw "The $Phase action was interrupted with safe code '$failureCode' (native error: $nativeError; target in bounds: $inBounds)."
    }
}

function Get-FrameManifest([object]$Response) {
    $text = [string]($Response.result.content |
            Where-Object { $_.type -eq 'text' -and $_.text -like 'RAPID_PC_FRAME *' } |
            Select-Object -First 1 -ExpandProperty text)
    $match = [Regex]::Match($text, '^RAPID_PC_FRAME (?<json>.+)$')
    if (-not $match.Success) {
        throw 'The driver response omitted its frame manifest.'
    }
    return $match.Groups['json'].Value | ConvertFrom-Json
}

function Get-FixtureProcess {
    $fixtureExecutable = Join-Path $root 'tools\PerformanceFixture\bin\win-x64\rapid-pc-performance-fixture.exe'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        foreach ($candidate in @(Get-Process -Name 'rapid-pc-performance-fixture' -ErrorAction SilentlyContinue)) {
            try {
                if ([string]::Equals(
                        [IO.Path]::GetFullPath($candidate.Path),
                        [IO.Path]::GetFullPath($fixtureExecutable),
                        [StringComparison]::OrdinalIgnoreCase) -and
                    $candidate.MainWindowHandle -ne 0) {
                    return $candidate
                }
            }
            catch [ComponentModel.Win32Exception] {
                # The candidate exited while it was inspected.
            }
            catch [InvalidOperationException] {
                # The candidate exited while it was inspected.
            }
        }
        Start-Sleep -Milliseconds 20
    }
    throw 'The performance fixture window was not available.'
}

function Convert-ScreenPointToAction([double]$X, [double]$Y, [object]$Manifest) {
    foreach ($display in @($Manifest.displays)) {
        $left = [double]$display.virtual_origin_px[0]
        $top = [double]$display.virtual_origin_px[1]
        $width = [double]$display.native_size_px[0]
        $height = [double]$display.native_size_px[1]
        if ($X -ge $left -and $X -lt ($left + $width) -and $Y -ge $top -and $Y -lt ($top + $height)) {
            return [ordered]@{
                type = 'click'
                display_id = [string]$display.display_id
                x = [Math]::Min(1000, [Math]::Max(0, [int][Math]::Round(1000 * ($X - $left) / ($width - 1))))
                y = [Math]::Min(1000, [Math]::Max(0, [int][Math]::Round(1000 * ($Y - $top) / ($height - 1))))
            }
        }
    }
    throw "Fixture point ($X, $Y) is outside the observed displays."
}

function Assert-FixtureHasFocus([Diagnostics.Process]$FixtureProcess) {
    $focused = [Windows.Automation.AutomationElement]::FocusedElement
    if ($null -eq $focused -or $focused.Current.ProcessId -ne $FixtureProcess.Id) {
        throw 'The performance fixture lost foreground focus; refusing to inject the action batch.'
    }
}

function Get-FixtureTarget {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$FixtureProcess,
        [Parameter(Mandatory)]
        [ValidateSet('initial', 'sequence')]
        [string]$Purpose
    )

    $rootElement = [Windows.Automation.AutomationElement]::FromHandle($FixtureProcess.MainWindowHandle)
    if ($FixtureId -eq 'form-tab-v1') {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit)
        return $rootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    }
    if ($Purpose -eq 'initial') {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)
        $buttons = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($button in $buttons) {
            if ($button.Current.Name -eq '5' -or $button.Current.Name -eq 'Tile 5') {
                return $button
            }
        }
    }
    return $rootElement
}

function Convert-ElementToAction([Windows.Automation.AutomationElement]$Element, [object]$Manifest) {
    if ($null -eq $Element) {
        throw 'The performance fixture did not expose its expected activation target.'
    }
    $rectangle = $Element.Current.BoundingRectangle
    return Convert-ScreenPointToAction `
        ($rectangle.Left + ($rectangle.Width / 2)) `
        ($rectangle.Top + ($rectangle.Height / 2)) `
        $Manifest
}

function New-FixtureActions([object]$Manifest) {
    if ($FixtureId -eq 'form-tab-v1') {
        return @(
            [ordered]@{ type = 'type'; text = 'Ada'; interval_ms = 0 },
            [ordered]@{ type = 'key'; keys = 'TAB' },
            [ordered]@{ type = 'type'; text = 'Lovelace'; interval_ms = 0 },
            [ordered]@{ type = 'key'; keys = 'TAB' },
            [ordered]@{ type = 'type'; text = 'Performance'; interval_ms = 0 },
            [ordered]@{ type = 'key'; keys = 'TAB' },
            [ordered]@{ type = 'type'; text = 'RPU-2048'; interval_ms = 0 },
            [ordered]@{ type = 'key'; keys = 'TAB' },
            [ordered]@{ type = 'type'; text = 'latency'; interval_ms = 0 },
            [ordered]@{ type = 'key'; keys = 'TAB' },
            [ordered]@{ type = 'key'; keys = 'ENTER' }
        )
    }

    $fixtureProcess = Get-FixtureProcess
    $rootElement = [Windows.Automation.AutomationElement]::FromHandle($fixtureProcess.MainWindowHandle)
    $sequence = @(12, 2, 15, 8, 1, 14, 4, 10, 7, 16, 3, 11, 6, 13, 9)
    $buttonCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $buttonElements = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $buttons = @{}
    foreach ($element in $buttonElements) {
        if ($element.Current.Name -like 'Tile *') {
            $buttons[$element.Current.Name] = $element
        }
    }
    $actions = [Collections.Generic.List[object]]::new()
    foreach ($number in $sequence) {
        $element = $buttons["Tile $number"]
        if ($null -eq $element) {
            throw "Could not find click-ladder tile $number through UI Automation."
        }
        $rectangle = $element.Current.BoundingRectangle
        $actions.Add((Convert-ScreenPointToAction `
                    ($rectangle.Left + ($rectangle.Width / 2)) `
                    ($rectangle.Top + ($rectangle.Height / 2)) `
                    $Manifest))
    }
    return @($actions)
}

function Wait-FixtureCompletion([string]$RunToken, [Diagnostics.Stopwatch]$WallClock) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            try {
                $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
                if ($state.runToken -eq $RunToken -and $state.status -eq 'completed') {
                    return [pscustomobject]@{
                        CompletionWallMs = $WallClock.Elapsed.TotalMilliseconds
                        FixtureActiveMs = [double]$state.activeElapsedMs
                    }
                }
            }
            catch {
                # Retry if the fixture is between its atomic temporary write and move.
            }
        }
        Start-Sleep -Milliseconds 1
    }
    return [pscustomobject]@{ CompletionWallMs = $null; FixtureActiveMs = $null }
}

$previousCaptureTier = $env:RAPID_PC_CAPTURE_TIER
$env:RAPID_PC_CAPTURE_TIER = $CaptureTier
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.WorkingDirectory = Split-Path $executable
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
[void]$process.Start()
$processStartedUtc = $process.StartTime.ToUniversalTime()
$rows = [Collections.Generic.List[object]]::new()
$requestId = 1

try {
    [void](Invoke-Mcp $requestId 'initialize' @{
            protocolVersion = '2025-11-25'
            capabilities = @{}
            clientInfo = @{ name = 'rapid-pc-use-actuator-baseline'; version = '1' }
        } $process)
    $requestId++

    $totalRuns = $WarmupRuns + $Repetitions
    for ($runNumber = 1; $runNumber -le $totalRuns; $runNumber++) {
        $isWarmup = $runNumber -le $WarmupRuns
        $repetition = $runNumber - $WarmupRuns
        $fixtureRepetition = [Math]::Max(1, $repetition)
        & $fixtureScript -Phase Reset -FixtureId $FixtureId -RunNumber $runNumber -Repetition $fixtureRepetition `
            -Model 'local-actuator' -Reasoning 'none' -CaptureTier $CaptureTier -AllowBackground
        $fixtureProcess = Get-FixtureProcess
        $runToken = "bench-$FixtureId-$runNumber"
        $wall = [Diagnostics.Stopwatch]::StartNew()
        $activationObserve = Invoke-Mcp $requestId 'tools/call' @{
            name = 'pc_observe'
            arguments = @{ begin_control = $true; capture_scope = 'full_desktop' }
        } $process
        $requestId++
        $activationManifest = Get-FrameManifest $activationObserve
        $activationTarget = Get-FixtureTarget -FixtureProcess $fixtureProcess -Purpose initial
        $activation = Invoke-Mcp $requestId 'tools/call' @{
                name = 'pc_act'
                arguments = @{
                    frame_id = [long]$activationManifest.frame_id
                    actions = @((Convert-ElementToAction $activationTarget $activationManifest))
                    settle_ms = 0
                    observe_after = $false
                }
            } $process
        $requestId++
        Assert-McpActionCompleted $activation 'fixture activation'
        Assert-FixtureHasFocus $fixtureProcess
        $observe = Invoke-Mcp $requestId 'tools/call' @{
            name = 'pc_observe'
            arguments = @{ begin_control = $true; capture_scope = $CaptureScope }
        } $process
        $requestId++
        $observedManifest = Get-FrameManifest $observe
        Assert-FixtureHasFocus $fixtureProcess
        $preparationStartedMs = $wall.Elapsed.TotalMilliseconds
        $actions = New-FixtureActions $observedManifest
        $actionPreparationMs = $wall.Elapsed.TotalMilliseconds - $preparationStartedMs
        $actStarted = $wall.Elapsed.TotalMilliseconds
        $act = Invoke-Mcp $requestId 'tools/call' @{
            name = 'pc_act'
            arguments = @{
                frame_id = [long]$observedManifest.frame_id
                actions = $actions
                settle_ms = $SettleMilliseconds
                observe_after = $true
            }
        } $process
        $requestId++
        Assert-McpActionCompleted $act 'main fixture'
        $actResponseWallMs = $wall.Elapsed.TotalMilliseconds
        $actedManifest = Get-FrameManifest $act
        $completion = Wait-FixtureCompletion $runToken $wall
        $verificationJson = [string](& $fixtureScript -Phase Verify -FixtureId $FixtureId `
                -RunNumber $runNumber -Repetition $fixtureRepetition -Model 'local-actuator' `
                -Reasoning 'none' -CaptureTier $CaptureTier)
        $verification = ConvertFrom-RapidPcFixtureVerification $verificationJson
        [void](Invoke-Mcp $requestId 'tools/call' @{ name = 'pc_stop'; arguments = @{} } $process)
        $requestId++

        if (-not $isWarmup) {
            $rows.Add([pscustomobject]@{
                Repetition = $repetition
                FixtureSuccess = $verification.Success
                UsefulActions = $verification.UsefulActions
                ErrorActions = $verification.ErrorActions
                InitialCaptureMs = [double]$observedManifest.total_capture_ms
                PostActionCaptureMs = [double]$actedManifest.total_capture_ms
                ActionPreparationMs = [Math]::Round($actionPreparationMs, 3)
                ActResponseMs = [Math]::Round($actResponseWallMs - $actStarted, 3)
                ObserveToActResponseMs = [Math]::Round($actResponseWallMs, 3)
                ObserveToFixtureCompleteMs = if ($null -eq $completion.CompletionWallMs) { $null } else {
                    [Math]::Round([double]$completion.CompletionWallMs, 3)
                }
                ActStartToFixtureCompleteMs = if ($null -eq $completion.CompletionWallMs) { $null } else {
                    [Math]::Round([double]$completion.CompletionWallMs - $actStarted, 3)
                }
                FixtureActiveMs = $completion.FixtureActiveMs
                ActionExecutionMs = $null
                SettleElapsedMs = $null
            })
        }
    }
}
finally {
    try {
        $process.StandardInput.Close()
    }
    catch [InvalidOperationException] {
    }
    if (-not $process.WaitForExit(5000)) {
        $process.Kill($true)
        [void]$process.WaitForExit(2000)
    }
    $env:RAPID_PC_CAPTURE_TIER = $previousCaptureTier
}

if ($process.ExitCode -ne 0) {
    $stderr = $process.StandardError.ReadToEnd().Trim()
    throw "The actuator benchmark driver exited with code $($process.ExitCode). Stderr: $stderr"
}

$actLogs = [Collections.Generic.List[object]]::new()
if (Test-Path -LiteralPath $logPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $logPath) {
        try {
            $entry = $line | ConvertFrom-Json
            if ($entry.pid -eq $process.Id -and
                ([DateTimeOffset]$entry.timestamp).UtcDateTime -ge $processStartedUtc -and
                $entry.event -eq 'tool.completed' -and
                $entry.tool -eq 'pc_act') {
                $actLogs.Add($entry)
            }
        }
        catch {
            # Ignore an older truncated log line.
        }
    }
}
$mainActLogs = @($actLogs | Where-Object { [int]$_.data.request.action_count -gt 1 })
if ($mainActLogs.Count -ne ($rows.Count + $WarmupRuns)) {
    throw "Expected $($rows.Count + $WarmupRuns) flushed main pc_act telemetry records, found $($mainActLogs.Count)."
}
for ($index = 0; $index -lt $rows.Count; $index++) {
    $log = $mainActLogs[$index + $WarmupRuns]
    $rows[$index].ActionExecutionMs = [Math]::Round([double]$log.data.result.action_execution_us / 1000, 3)
    $rows[$index].SettleElapsedMs = [Math]::Round([double]$log.data.result.settle_elapsed_us / 1000, 3)
}

$successful = @($rows | Where-Object FixtureSuccess)
$completionValues = @($successful | Where-Object { $null -ne $_.ObserveToFixtureCompleteMs } |
        ForEach-Object { [double]$_.ObserveToFixtureCompleteMs })
$activeValues = @($successful | Where-Object { $null -ne $_.FixtureActiveMs } |
        ForEach-Object { [double]$_.FixtureActiveMs })
$actionValues = @($rows | ForEach-Object { [double]$_.ActionExecutionMs })
$captureValues = @($rows | ForEach-Object { [double]$_.PostActionCaptureMs })
$settleValues = @($rows | ForEach-Object { [double]$_.SettleElapsedMs })
$totalUsefulActions = [double](($successful | Measure-Object UsefulActions -Sum).Sum)
$totalCompletionMs = [double](($successful | Measure-Object ObserveToFixtureCompleteMs -Sum).Sum)
$summary = [pscustomobject]@{
    Runs = $rows.Count
    SuccessfulRuns = $successful.Count
    SuccessRatePercent = [Math]::Round(100 * $successful.Count / $rows.Count, 1)
    SuccessfulUsefulActionsPerMinute = if ($totalCompletionMs -le 0) { $null } else {
        [Math]::Round(60000 * $totalUsefulActions / $totalCompletionMs, 2)
    }
    P50ObserveToFixtureCompleteMs = if ($completionValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $completionValues 0.50), 3)
    }
    P95ObserveToFixtureCompleteMs = if ($completionValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $completionValues 0.95), 3)
    }
    P50FixtureActiveMs = if ($activeValues.Count -eq 0) { $null } else {
        [Math]::Round((Get-RapidPcBenchmarkPercentile $activeValues 0.50), 3)
    }
    P50ActionExecutionMs = [Math]::Round((Get-RapidPcBenchmarkPercentile $actionValues 0.50), 3)
    P50PostActionCaptureMs = [Math]::Round((Get-RapidPcBenchmarkPercentile $captureValues 0.50), 3)
    P50SettleElapsedMs = [Math]::Round((Get-RapidPcBenchmarkPercentile $settleValues 0.50), 3)
}

$artifact = [ordered]@{
    SchemaVersion = 1
    CreatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Commit = [string](& git -C $root rev-parse HEAD)
    FixtureId = $FixtureId
    Repetitions = $Repetitions
    WarmupRuns = $WarmupRuns
    CaptureTier = $CaptureTier
    CaptureScope = $CaptureScope
    SettleMilliseconds = $SettleMilliseconds
    DisplayCount = if ($rows.Count -eq 0) { 0 } else { @($observedManifest.displays).Count }
    Summary = $summary
    Rows = @($rows)
}
$artifact | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8

[pscustomobject]@{
    Fixture = $FixtureId
    CaptureScope = $CaptureScope
    Runs = $summary.Runs
    SuccessRatePercent = $summary.SuccessRatePercent
    SuccessfulUsefulActionsPerMinute = $summary.SuccessfulUsefulActionsPerMinute
    P50CompletionMs = $summary.P50ObserveToFixtureCompleteMs
    P95CompletionMs = $summary.P95ObserveToFixtureCompleteMs
    P50ActionExecutionMs = $summary.P50ActionExecutionMs
    P50PostActionCaptureMs = $summary.P50PostActionCaptureMs
    Artifact = $OutputPath
}
