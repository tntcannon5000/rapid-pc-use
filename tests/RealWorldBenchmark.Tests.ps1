$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'scripts\real-world\scenario-catalog.ps1')
. (Join-Path $root 'scripts\real-world\invoke-run.ps1')

function Assert-Test {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

$config = [pscustomobject]@{
    discord = [pscustomobject]@{
        contact = 'PRIVATE-CONTACT-CANARY'
        server = 'PRIVATE-SERVER-CANARY'
        messagePrefix = 'PRIVATE-PREFIX-CANARY'
    }
    youtube = [pscustomobject]@{ query = 'PRIVATE-QUERY-CANARY' }
    amazon = [pscustomobject]@{
        sinceDate = '2026-07-01'
        expectedItemCount = 15
        expectedReturnedCount = 1
    }
}

$discord = New-RapidPcRealWorldScenario -Id 'discord-dm' -Config $config -Nonce 'scope-test'
$youtube = New-RapidPcRealWorldScenario -Id 'youtube' -Config $config -Nonce 'scope-test'
$amazon = New-RapidPcRealWorldScenario -Id 'amazon-orders' -Config $config -Nonce 'scope-test'

$discordScope = New-RapidPcBenchmarkScope -Scenario $discord -SupportsRemoteContentScope $true
Assert-Test ($discordScope.allow_external_communication -eq $true) 'Discord scope omitted external communication.'
Assert-Test ($discordScope.allow_remote_content_changes -eq $true) 'Discord scope omitted remote mutation.'
Assert-Test ($discordScope.allow_local_deletion -eq $false) 'New-contract Discord scope granted local deletion.'
$legacyDiscordScope = New-RapidPcBenchmarkScope -Scenario $discord -SupportsRemoteContentScope $false
Assert-Test ($legacyDiscordScope.allow_local_deletion -eq $true) 'Legacy Discord scope cannot authorize its remote DELETE chord.'
Assert-Test (-not $legacyDiscordScope.ContainsKey('allow_remote_content_changes')) 'Legacy scope sent an unsupported field.'

$youtubeScope = New-RapidPcBenchmarkScope -Scenario $youtube -SupportsRemoteContentScope $true
Assert-Test ($youtubeScope.allow_external_communication -eq $false) 'YouTube scope granted external communication.'
Assert-Test ($youtubeScope.allow_remote_content_changes -eq $true) 'YouTube scope omitted remote state changes.'
Assert-Test ($youtubeScope.allow_local_deletion -eq $false) 'YouTube scope granted local deletion.'

$amazonScope = New-RapidPcBenchmarkScope -Scenario $amazon -SupportsRemoteContentScope $true
Assert-Test ($amazonScope.Values -notcontains $true) 'Read-only Amazon scope granted mutating authority.'

Assert-RapidPcBenchmarkSchedulePolicy `
    -Scenarios @('amazon-orders') `
    -Models @('gpt-5.6-luna', 'gpt-5.6-terra', 'gpt-5.6-sol') `
    -Repetitions 1 `
    -AllowAccountMutations $false
$multiMutationRejected = $false
try {
    Assert-RapidPcBenchmarkSchedulePolicy `
        -Scenarios @('discord-dm') `
        -Models @('gpt-5.6-luna', 'gpt-5.6-terra') `
        -Repetitions 1 `
        -AllowAccountMutations $true
}
catch { $multiMutationRejected = $true }
Assert-Test $multiMutationRejected 'Unverifiable multi-model mutation schedule was accepted.'

$terminalResponse = [pscustomobject]@{
    result = [pscustomobject]@{
        content = @([pscustomobject]@{ type = 'text'; text = 'USER_TAKEOVER: user owns the PC' })
        isError = $false
    }
}
$terminalCaught = $false
try { Assert-RapidPcMcpResponseIsContinuable -Response $terminalResponse }
catch {
    $terminalCaught = $_.Exception.Data['RapidPcBenchmarkTerminal'] -eq 'user_takeover'
}
Assert-Test $terminalCaught 'Physical takeover did not become a terminal benchmark abort.'

$driverFailure = [pscustomobject]@{
    result = [pscustomobject]@{
        content = @([pscustomobject]@{ type = 'text'; text = 'RAPID_PC_USE_FAILURE: bounded failure' })
        isError = $true
    }
}
$failureCaught = $false
try { Assert-RapidPcMcpResponseIsContinuable -Response $driverFailure }
catch {
    $failureCaught = $_.Exception.Data['RapidPcBenchmarkTerminal'] -eq 'driver_failure'
}
Assert-Test $failureCaught 'Driver failure did not become a terminal benchmark abort.'

$ownedProcess = Start-Process `
    -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') `
    -WindowStyle Hidden `
    -PassThru
try {
    Assert-Test (Stop-RapidPcBenchmarkDriver -Process $ownedProcess -GraceMilliseconds 10) 'Compatibility driver cleanup did not stop its owned process.'
}
finally {
    if (-not $ownedProcess.HasExited) { Stop-Process -Id $ownedProcess.Id -Force -ErrorAction SilentlyContinue }
    $ownedProcess.Dispose()
}

$amazonVerification = Get-RapidPcRealWorldVerification `
    -Scenario $amazon `
    -Config $config `
    -Status 'completed' `
    -Summary "$($amazon.Marker) items=15 returned=1" `
    -ApplicationClosed $true
Assert-Test $amazonVerification.Success 'Known-answer Amazon completion was not verified.'
Assert-Test $amazonVerification.StateIsolationVerified 'Read-only Amazon did not preserve schedule isolation.'

$discordVerification = Get-RapidPcRealWorldVerification `
    -Scenario $discord `
    -Config $config `
    -Status 'completed' `
    -Summary $discord.Marker `
    -ApplicationClosed $true
Assert-Test (-not $discordVerification.Success) 'Model-attested Discord cleanup was mislabeled verified.'
Assert-Test $discordVerification.ProvisionalSuccess 'Model-attested Discord cleanup was not retained as provisional.'
Assert-Test (-not $discordVerification.StateIsolationVerified) 'Discord cleanup incorrectly allowed state carryover.'

$safeArtifact = [pscustomobject]@{ Rows = @([pscustomobject]@{ Scenario = 'amazon-orders'; ObservedItemCount = 15 }) }
Assert-RapidPcBenchmarkArtifactPrivacy -Artifact $safeArtifact -Config $config
$leakCaught = $false
try {
    Assert-RapidPcBenchmarkArtifactPrivacy `
        -Artifact ([pscustomobject]@{ Rows = @([pscustomobject]@{ Detail = $config.discord.contact }) }) `
        -Config $config
}
catch { $leakCaught = $true }
Assert-Test $leakCaught 'Artifact privacy guard did not reject a private configuration value.'

Write-Host 'Real-world benchmark dry-run contract tests passed.'
