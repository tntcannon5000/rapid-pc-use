[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BeforePath,
    [Parameter(Mandatory)][string]$AfterPath,
    [string[]]$AfterSupplementPath = @(),
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-NumericDelta {
    param([object]$Before, [object]$After)
    if ($null -eq $Before -or $null -eq $After) { return $null }
    return [Math]::Round(([double]$After - [double]$Before), 3)
}

function Test-VerifiedSuccess {
    param([Parameter(Mandatory)][object]$Row)
    if ([string]$Row.Scenario -ne 'amazon-orders') { return $false }
    return [bool]$Row.Success
}

function Test-ProvisionalSuccess {
    param([Parameter(Mandatory)][object]$Row)
    $property = $Row.PSObject.Properties['ProvisionalSuccess']
    if ($null -ne $property) { return [bool]$property.Value }
    return [string]$Row.Scenario -ne 'amazon-orders' -and [bool]$Row.Success
}

function Get-CellKey {
    param([Parameter(Mandatory)][object]$Row)
    return '{0}|{1}|{2}' -f [string]$Row.Scenario, [string]$Row.Model, [int]$Row.Repetition
}

function Get-SortedDimension {
    param([object[]]$Values)
    return (@($Values | ForEach-Object { [string]$_ } | Sort-Object) -join '|')
}

function Assert-CompatibleArtifacts {
    param([Parameter(Mandatory)][object]$Before, [Parameter(Mandatory)][object]$After)

    if ([int]$Before.SchemaVersion -ne [int]$After.SchemaVersion) {
        throw 'Benchmark artifacts use different schema versions.'
    }
    foreach ($property in @('Reasoning', 'ServiceTier', 'CaptureTier', 'Repetitions', 'RandomSeed')) {
        if ([string]$Before.$property -ne [string]$After.$property) {
            throw "Benchmark artifacts differ in $property."
        }
    }
    foreach ($property in @('Models', 'Scenarios')) {
        if ((Get-SortedDimension @($Before.$property)) -ne (Get-SortedDimension @($After.$property))) {
            throw "Benchmark artifacts differ in $property coverage."
        }
    }
    foreach ($artifact in @($Before, $After)) {
        $aborted = $artifact.PSObject.Properties['ScheduleAborted']
        if ($null -ne $aborted -and [bool]$aborted.Value) {
            throw 'Cannot aggregate an artifact whose schedule aborted before exact state isolation was established.'
        }
    }
}

function Assert-CompatibleSupplement {
    param([Parameter(Mandatory)][object]$Base, [Parameter(Mandatory)][object]$Supplement)

    if ([int]$Base.SchemaVersion -ne [int]$Supplement.SchemaVersion) {
        throw 'A supplement uses a different benchmark schema version.'
    }
    foreach ($property in @('Reasoning', 'ServiceTier', 'CaptureTier', 'Repetitions', 'RandomSeed')) {
        if ([string]$Base.$property -ne [string]$Supplement.$property) {
            throw "A supplement differs from the after artifact in $property."
        }
    }
    $aborted = $Supplement.PSObject.Properties['ScheduleAborted']
    if ($null -ne $aborted -and [bool]$aborted.Value) {
        throw 'Cannot merge a supplement whose schedule aborted without verified state isolation.'
    }
    $baseModels = @($Base.Models | ForEach-Object { [string]$_ })
    $baseScenarios = @($Base.Scenarios | ForEach-Object { [string]$_ })
    foreach ($row in @($Supplement.Rows)) {
        if ([string]$row.Model -notin $baseModels -or [string]$row.Scenario -notin $baseScenarios -or
            [int]$row.Repetition -lt 1 -or [int]$row.Repetition -gt [int]$Base.Repetitions) {
            throw "A supplement contains an out-of-matrix cell '$(Get-CellKey $row)'."
        }
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'benchmark-results\real-world-comparison.json'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

$before = Get-Content -LiteralPath ([IO.Path]::GetFullPath($BeforePath)) -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath ([IO.Path]::GetFullPath($AfterPath)) -Raw | ConvertFrom-Json
Assert-CompatibleArtifacts -Before $before -After $after
$afterRows = [Collections.Generic.List[object]]::new()
foreach ($row in @($after.Rows)) { $afterRows.Add($row) }
foreach ($supplementPath in $AfterSupplementPath) {
    $supplement = Get-Content -LiteralPath ([IO.Path]::GetFullPath($supplementPath)) -Raw | ConvertFrom-Json
    Assert-CompatibleSupplement -Base $after -Supplement $supplement
    foreach ($replacement in @($supplement.Rows)) {
        $existing = @($afterRows | Where-Object {
            $_.Scenario -eq $replacement.Scenario -and
            $_.Model -eq $replacement.Model -and
            $_.Repetition -eq $replacement.Repetition
        }) | Select-Object -First 1
        if ($null -ne $existing -and $existing.Status -eq 'infrastructure_failure') {
            [void]$afterRows.Remove($existing)
            $afterRows.Add($replacement)
        }
        elseif ($null -eq $existing) {
            $afterRows.Add($replacement)
        }
    }
}

$beforeKeys = @{}
foreach ($row in @($before.Rows)) {
    $key = Get-CellKey $row
    if ($beforeKeys.ContainsKey($key)) { throw "Before artifact contains duplicate cell '$key'." }
    $beforeKeys[$key] = $true
}
$afterKeys = @{}
foreach ($row in @($afterRows)) {
    $key = Get-CellKey $row
    if ($afterKeys.ContainsKey($key)) { throw "After artifact contains duplicate cell '$key'." }
    $afterKeys[$key] = $true
}
$missingAfter = @($beforeKeys.Keys | Where-Object { -not $afterKeys.ContainsKey($_) })
$extraAfter = @($afterKeys.Keys | Where-Object { -not $beforeKeys.ContainsKey($_) })
if ($missingAfter.Count -gt 0 -or $extraAfter.Count -gt 0) {
    throw "Benchmark cell coverage differs (missing after: $($missingAfter.Count); extra after: $($extraAfter.Count))."
}

$comparisons = [Collections.Generic.List[object]]::new()
foreach ($beforeRow in @($before.Rows)) {
    $afterRow = @($afterRows | Where-Object {
        $_.Scenario -eq $beforeRow.Scenario -and
        $_.Model -eq $beforeRow.Model -and
        $_.Repetition -eq $beforeRow.Repetition
    }) | Select-Object -First 1
    if ($null -eq $afterRow) { continue }
    $comparisons.Add([pscustomobject]@{
        Scenario = [string]$beforeRow.Scenario
        Model = [string]$beforeRow.Model
        BeforeStatus = [string]$beforeRow.Status
        AfterStatus = [string]$afterRow.Status
        BeforeSuccess = Test-VerifiedSuccess $beforeRow
        AfterSuccess = Test-VerifiedSuccess $afterRow
        BeforeProvisionalSuccess = Test-ProvisionalSuccess $beforeRow
        AfterProvisionalSuccess = Test-ProvisionalSuccess $afterRow
        BeforePromptToResultMs = $beforeRow.PromptToResultWallMs
        AfterPromptToResultMs = $afterRow.PromptToResultWallMs
        PromptToResultDeltaMs = Get-NumericDelta $beforeRow.PromptToResultWallMs $afterRow.PromptToResultWallMs
        BeforeFirstDecisionMs = $beforeRow.FirstDecisionDeltaMs
        AfterFirstDecisionMs = $afterRow.FirstDecisionDeltaMs
        FirstDecisionDeltaMs = Get-NumericDelta $beforeRow.FirstDecisionDeltaMs $afterRow.FirstDecisionDeltaMs
        BeforeOutputFillMs = $beforeRow.OutputFillMs
        AfterOutputFillMs = $afterRow.OutputFillMs
        OutputFillDeltaMs = Get-NumericDelta $beforeRow.OutputFillMs $afterRow.OutputFillMs
        BeforeModelTurns = $beforeRow.ModelTurns
        AfterModelTurns = $afterRow.ModelTurns
        BeforeActions = $beforeRow.ActionsExecuted
        AfterActions = $afterRow.ActionsExecuted
        AfterPointerPacingMs = if ($null -ne $afterRow.PSObject.Properties['TotalPointerPacingMs']) { $afterRow.TotalPointerPacingMs } else { $null }
        AfterInitialLaunchMs = if ($null -ne $afterRow.PSObject.Properties['InitialLaunchMs']) { $afterRow.InitialLaunchMs } else { $null }
    })
}

$validBefore = @($before.Rows | Where-Object { $_.Status -ne 'infrastructure_failure' })
$validAfter = @($afterRows | Where-Object { $_.Status -ne 'infrastructure_failure' })
$artifact = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedAt = [DateTimeOffset]::UtcNow.ToString('O')
    BeforeLabel = [string]$before.Label
    AfterLabel = [string]$after.Label
    BeforeValidRuns = $validBefore.Count
    AfterValidRuns = $validAfter.Count
    BeforeSuccessfulRuns = @($validBefore | Where-Object { Test-VerifiedSuccess $_ }).Count
    AfterSuccessfulRuns = @($validAfter | Where-Object { Test-VerifiedSuccess $_ }).Count
    BeforeProvisionalRuns = @($validBefore | Where-Object { Test-ProvisionalSuccess $_ }).Count
    AfterProvisionalRuns = @($validAfter | Where-Object { Test-ProvisionalSuccess $_ }).Count
    Privacy = 'Comparison contains only bounded statuses, counts, and timings from the privacy-filtered source artifacts.'
    Rows = @($comparisons)
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[IO.File]::WriteAllText($OutputPath, ($artifact | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$comparisons | Select-Object Scenario, Model, BeforeStatus, AfterStatus, BeforeSuccess, AfterSuccess, BeforeProvisionalSuccess, AfterProvisionalSuccess, BeforePromptToResultMs, AfterPromptToResultMs, PromptToResultDeltaMs, BeforeFirstDecisionMs, AfterFirstDecisionMs, BeforeOutputFillMs, AfterOutputFillMs | Format-Table -AutoSize
Write-Host "Before: $($artifact.BeforeSuccessfulRuns)/$($artifact.BeforeValidRuns) successful; after: $($artifact.AfterSuccessfulRuns)/$($artifact.AfterValidRuns) successful."
Write-Host "Provisional model-attested cleanup: before $($artifact.BeforeProvisionalRuns); after $($artifact.AfterProvisionalRuns)."
Write-Host "Comparison artifact: $OutputPath"
