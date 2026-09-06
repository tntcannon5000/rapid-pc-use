[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Reset', 'Verify', 'Cleanup')]
    [string]$Phase,
    [Parameter(Mandatory)]
    [ValidateSet('click-ladder-v1', 'form-tab-v1')]
    [string]$FixtureId,
    [Parameter(Mandatory)]
    [ValidateRange(1, 1000000)]
    [int]$RunNumber,
    [Parameter(Mandatory)]
    [ValidateRange(1, 100)]
    [int]$Repetition,
    [Parameter(Mandatory)]
    [string]$Model,
    [Parameter(Mandatory)]
    [string]$Reasoning,
    [Parameter(Mandatory)]
    [ValidateSet('720', '900')]
    [string]$CaptureTier,
    [switch]$AllowBackground
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $root 'tools\PerformanceFixture\bin\win-x64\rapid-pc-performance-fixture.exe'
$stateDirectory = Join-Path $env:LOCALAPPDATA 'RapidPcUse\PerformanceFixture'
$statePath = Join-Path $stateDirectory 'state.json'
$runToken = "bench-$FixtureId-$RunNumber"
$processName = 'rapid-pc-performance-fixture'

# These values are accepted to match the generic benchmark fixture contract.
$_ = $Repetition
$_ = $Model
$_ = $Reasoning
$_ = $CaptureTier

function Stop-PerformanceFixture {
    foreach ($candidate in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        try {
            if ([string]::Equals(
                    [IO.Path]::GetFullPath($candidate.Path),
                    [IO.Path]::GetFullPath($executable),
                    [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $candidate.Id -Force
                [void]$candidate.WaitForExit(2000)
            }
        }
        catch [ComponentModel.Win32Exception] {
            # Ignore a fixture process that exited between enumeration and inspection.
        }
        catch [InvalidOperationException] {
            # Ignore a fixture process that exited between enumeration and inspection.
        }
    }
}

function Write-VerificationResult {
    param(
        [bool]$Success,
        [long]$UsefulActions,
        [long]$ErrorActions,
        [long]$FixtureElapsedMs,
        [string]$FailureCategory
    )

    [pscustomobject]@{
        success = $Success
        usefulActions = $UsefulActions
        errorActions = $ErrorActions
        fixtureElapsedMs = $FixtureElapsedMs
        failureCategory = if ($Success) { $null } else { $FailureCategory }
    } | ConvertTo-Json -Compress
}

function Set-PerformanceFixtureFocus {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$Fixture
    )

    $rootElement = [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
    if ($Fixture -eq 'form-tab-v1') {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit)
        $target = $rootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    }
    else {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)
        $target = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) |
            Where-Object { $_.Current.Name -eq '5' -or $_.Current.Name -eq 'Tile 5' } |
            Select-Object -First 1
    }
    if ($null -eq $target) {
        throw 'The performance fixture did not expose its expected initial focus target.'
    }
    $target.SetFocus()

    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds(500)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $focused = [Windows.Automation.AutomationElement]::FocusedElement
        if ($null -ne $focused -and $focused.Current.ProcessId -eq $Process.Id) {
            return
        }
        Start-Sleep -Milliseconds 10
    }
    throw 'The performance fixture did not retain keyboard focus.'
}

if ($Phase -eq 'Cleanup') {
    Stop-PerformanceFixture
    return
}

if ($Phase -eq 'Reset') {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Build the performance fixture before benchmarking: $executable"
    }

    Stop-PerformanceFixture
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        Remove-Item -LiteralPath $statePath -Force
    }

    $fixtureProcess = Start-Process `
        -FilePath $executable `
        -ArgumentList @('--fixture', $FixtureId, '--run-token', $runToken) `
        -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($fixtureProcess.HasExited) {
            throw "The performance fixture exited during reset with code $($fixtureProcess.ExitCode)."
        }
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            $state = $null
            try {
                $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
            }
            catch {
                # The fixture may be between its atomic temporary write and move.
            }
            if ($null -ne $state -and
                $state.fixtureId -eq $FixtureId -and
                $state.runToken -eq $runToken -and
                $state.status -eq 'ready') {
                $shell = New-Object -ComObject WScript.Shell
                if (-not $shell.AppActivate($fixtureProcess.Id) -and -not $AllowBackground) {
                    throw 'The performance fixture window could not be activated.'
                }
                try {
                    Set-PerformanceFixtureFocus -Process $fixtureProcess -Fixture $FixtureId
                }
                catch {
                    if (-not $AllowBackground) {
                        throw
                    }
                }
                Start-Sleep -Milliseconds 100
                return
            }
        }

        Start-Sleep -Milliseconds 50
    }

    Stop-PerformanceFixture
    throw 'The performance fixture did not become ready within ten seconds.'
}

$expectedUsefulActions = if ($FixtureId -eq 'click-ladder-v1') { 16L } else { 6L }
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    Stop-PerformanceFixture
    Write-VerificationResult $false 0 0 0 'state_missing'
    return
}

try {
    $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
}
catch {
    Stop-PerformanceFixture
    Write-VerificationResult $false 0 0 0 'state_invalid'
    return
}

$usefulActions = if ($null -eq $state.usefulActions) { 0L } else { [long]$state.usefulActions }
$errorActions = if ($null -eq $state.incorrectActions) { 0L } else { [long]$state.incorrectActions }
$fixtureElapsedMs = if ($null -eq $state.activeElapsedMs) { 0L } else { [long]$state.activeElapsedMs }
$success = $state.fixtureId -eq $FixtureId -and
    $state.runToken -eq $runToken -and
    $state.status -eq 'completed' -and
    $usefulActions -eq $expectedUsefulActions
$failureCategory = if ($state.fixtureId -ne $FixtureId -or $state.runToken -ne $runToken) {
    'token_mismatch'
}
elseif ($state.status -ne 'completed') {
    'incomplete'
}
else {
    'useful_action_mismatch'
}

Stop-PerformanceFixture
Write-VerificationResult $success $usefulActions $errorActions $fixtureElapsedMs $failureCategory
