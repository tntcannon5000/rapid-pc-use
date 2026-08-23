Set-StrictMode -Version Latest

function Import-RapidPcRealWorldConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Real-world benchmark config not found: $resolved"
    }

    $config = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    foreach ($section in @('discord', 'youtube', 'amazon')) {
        if ($null -eq $config.PSObject.Properties[$section]) {
            throw "Benchmark config is missing '$section'."
        }
    }

    Assert-RapidPcConfigText $config.discord.contact 'discord.contact' 128
    Assert-RapidPcConfigText $config.discord.server 'discord.server' 128
    Assert-RapidPcConfigText $config.discord.messagePrefix 'discord.messagePrefix' 32
    Assert-RapidPcConfigText $config.youtube.query 'youtube.query' 256
    Assert-RapidPcConfigText $config.amazon.sinceDate 'amazon.sinceDate' 10
    $parsedDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
        [string]$config.amazon.sinceDate,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$parsedDate)) {
        throw 'amazon.sinceDate must use yyyy-MM-dd.'
    }

    foreach ($property in @('expectedItemCount', 'expectedReturnedCount')) {
        $parsed = 0
        if (-not [int]::TryParse([string]$config.amazon.$property, [ref]$parsed) -or $parsed -lt 0 -or $parsed -gt 10000) {
            throw "amazon.$property must be an integer from 0 to 10000."
        }
    }

    return $config
}

function Assert-RapidPcConfigText {
    param([object]$Value, [string]$Name, [int]$Maximum)

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt $Maximum -or $text -match '[\x00-\x1f]') {
        throw "$Name must contain 1 to $Maximum printable characters."
    }
}

function Assert-RapidPcBenchmarkSchedulePolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Scenarios,
        [Parameter(Mandatory)][string[]]$Models,
        [Parameter(Mandatory)][int]$Repetitions,
        [Parameter(Mandatory)][bool]$AllowAccountMutations
    )

    $mutationScenarioCount = @($Scenarios | Where-Object { $_ -in @('discord-dm', 'youtube') }).Count
    $mutationCellCount = $mutationScenarioCount * $Models.Count * $Repetitions
    if ($mutationCellCount -gt 0 -and -not $AllowAccountMutations) {
        throw 'Discord and YouTube cells mutate account state. Pass -AllowAccountMutations explicitly.'
    }
    if ($mutationCellCount -gt 1) {
        throw 'Mutation scenarios currently require one scenario/model/repetition per invocation because account-state restoration is not independently verified. Run the read-only Amazon matrix separately.'
    }
}

function New-RapidPcRealWorldScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('discord-dm', 'youtube', 'amazon-orders')][string]$Id,
        [Parameter(Mandatory)][object]$Config,
        [Parameter(Mandatory)][string]$Nonce
    )

    switch ($Id) {
        'discord-dm' {
            $temporaryMessage = '{0} {1}' -f $Config.discord.messagePrefix, $Nonce
            return [pscustomobject]@{
                Id = $Id
                Application = 'discord'
                LaunchUri = 'discord:'
                Marker = "DISCORD_DM_COMPLETE $Nonce"
                MeaningfulCheckpoints = 5
                AllowExternalCommunication = $true
                AllowRemoteContentChanges = $true
                AllowLegacyLocalDeletion = $true
                MutatesAccountState = $true
                ExecutionContext = 'Trusted fast route: direct Discord application launch was requested. Use the visible app search to reach the exact contact named in the task; do not spend a model turn navigating Start.'
                Task = @"
Starting with Discord fully closed, use the visible Discord application to message the test account "$($Config.discord.contact)" with exactly "$temporaryMessage". Verify that the message was sent, delete that exact temporary message, and verify it is gone. Then navigate to the "$($Config.discord.server)" Discord server and visibly verify that server before fully quitting Discord. Do not merely close its window: Discord must be fully exited. Do not leave any temporary benchmark message behind. Finish with the exact summary marker: DISCORD_DM_COMPLETE $Nonce
"@.Trim()
            }
        }
        'youtube' {
            return [pscustomobject]@{
                Id = $Id
                Application = 'chrome'
                LaunchUri = 'https://www.youtube.com/'
                Marker = "YOUTUBE_STATE_RESTORED $Nonce"
                MeaningfulCheckpoints = 5
                AllowExternalCommunication = $false
                AllowRemoteContentChanges = $true
                AllowLegacyLocalDeletion = $false
                MutatesAccountState = $true
                ExecutionContext = 'Trusted fast route: the browser was launched directly to YouTube. Start from the visible target page and prefer search/address and keyboard controls over Windows app navigation.'
                Task = @"
Starting with Chrome closed, open YouTube and search for "$($Config.youtube.query)". Open the intended video and play it. Inspect and remember its current Like state and playlist membership. Make the video liked if needed, then inspect the playlist chooser carefully enough to determine which playlists currently contain it. Restore the video's original Like state and exact original playlist membership before closing Chrome normally. Do not finish with a visible Chrome window. Finish with the exact summary marker: YOUTUBE_STATE_RESTORED $Nonce
"@.Trim()
            }
        }
        'amazon-orders' {
            return [pscustomobject]@{
                Id = $Id
                Application = 'chrome'
                LaunchUri = 'https://www.amazon.co.uk/gp/css/order-history'
                Marker = "AMAZON_COUNT_COMPLETE $Nonce"
                MeaningfulCheckpoints = 2
                AllowExternalCommunication = $false
                AllowRemoteContentChanges = $false
                AllowLegacyLocalDeletion = $false
                MutatesAccountState = $false
                ExecutionContext = 'Trusted fast route: the browser was launched directly to Amazon Orders. Remain read-only; count item quantities on or after the task date and inspect additional order pages only when needed.'
                Task = @"
Starting with Chrome closed, open Amazon and use the signed-in Orders interface to count how many individual items were ordered on or after $($Config.amazon.sinceDate). Count item quantities, not merely order cards. Do not buy, cancel, return, review, or otherwise change anything. Also determine how many of those items are visibly marked returned if the interface makes that available. Close Chrome normally when finished. Use this exact summary format with numeric values: AMAZON_COUNT_COMPLETE $Nonce items=<number> returned=<number>
"@.Trim()
            }
        }
    }
}

function Reset-RapidPcRealWorldScenario {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Scenario)

    if ($Scenario.Application -eq 'discord') {
        Stop-RapidPcBenchmarkProcess -Name 'Discord'
        return
    }

    Close-RapidPcVisibleWindows -Name 'chrome'
}

function Test-RapidPcRealWorldScenario {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Scenario,
        [Parameter(Mandatory)][object]$Config,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Summary
    )

    $applicationClosed = if ($Scenario.Application -eq 'discord') {
        @(Get-Process -Name 'Discord' -ErrorAction SilentlyContinue).Count -eq 0
    }
    else {
        -not (Test-RapidPcVisibleWindow -Name 'chrome')
    }

    return Get-RapidPcRealWorldVerification `
        -Scenario $Scenario `
        -Config $Config `
        -Status $Status `
        -Summary $Summary `
        -ApplicationClosed $applicationClosed
}

function Get-RapidPcRealWorldVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Scenario,
        [Parameter(Mandatory)][object]$Config,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Summary,
        [Parameter(Mandatory)][bool]$ApplicationClosed
    )

    $observedItems = $null
    $observedReturned = $null
    $countMatched = $true
    $returnBonus = $false
    if ($Scenario.Id -eq 'amazon-orders') {
        $pattern = '{0} items=(\d+) returned=(\d+)' -f [Regex]::Escape([string]$Scenario.Marker)
        $match = [Regex]::Match($Summary, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($match.Success) {
            $observedItems = [int]$match.Groups[1].Value
            $observedReturned = [int]$match.Groups[2].Value
        }
        $countMatched = $match.Success -and $observedItems -eq [int]$Config.amazon.expectedItemCount
        $returnBonus = $match.Success -and $observedReturned -eq [int]$Config.amazon.expectedReturnedCount
    }

    $markerSeen = $Summary.IndexOf([string]$Scenario.Marker, [StringComparison]::Ordinal) -ge 0
    $modelAttestedCompletion = $Status -eq 'completed' -and $markerSeen -and $ApplicationClosed -and $countMatched
    $verifiedSuccess = $Scenario.Id -eq 'amazon-orders' -and $modelAttestedCompletion
    $provisionalSuccess = [bool]$Scenario.MutatesAccountState -and $modelAttestedCompletion
    $verificationLevel = if ($verifiedSuccess) { 'known_answer_match' }
    elseif ($provisionalSuccess) { 'model_attested_cleanup' }
    else { 'not_verified' }
    $failureCategory = if ($verifiedSuccess) { $null }
    elseif ($provisionalSuccess) { 'requires_independent_verification' }
    elseif ($Status -ne 'completed') { "agent_$Status" }
    elseif (-not $markerSeen) { 'completion_marker_missing' }
    elseif (-not $ApplicationClosed) { 'application_not_closed' }
    elseif (-not $countMatched) { 'amazon_count_mismatch' }
    else { 'unknown' }

    return [pscustomobject]@{
        Success = $verifiedSuccess
        ProvisionalSuccess = $provisionalSuccess
        VerificationLevel = $verificationLevel
        StateIsolationVerified = -not [bool]$Scenario.MutatesAccountState
        FailureCategory = $failureCategory
        CompletionMarkerSeen = $markerSeen
        ApplicationClosed = $ApplicationClosed
        ObservedItemCount = $observedItems
        ObservedReturnedCount = $observedReturned
        ReturnedCountBonus = $returnBonus
        MeaningfulCheckpoints = [int]$Scenario.MeaningfulCheckpoints
    }
}

function Assert-RapidPcBenchmarkArtifactPrivacy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Artifact,
        [Parameter(Mandatory)][object]$Config
    )

    $json = $Artifact | ConvertTo-Json -Depth 12 -Compress
    $privateValues = @(
        [string]$Config.discord.contact,
        [string]$Config.discord.server,
        [string]$Config.discord.messagePrefix,
        [string]$Config.youtube.query
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($privateValue in $privateValues) {
        if ($json.IndexOf($privateValue, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw 'The benchmark artifact privacy projection retained a private configuration value.'
        }
    }
}

function Stop-RapidPcBenchmarkProcess {
    param([Parameter(Mandatory)][string]$Name)

    $processes = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            # Multi-process apps tear down child processes concurrently. Treat
            # an already-exited PID as successful idempotent cleanup.
            if ($null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
                throw
            }
        }
    }
    if ($processes.Count -gt 0) {
        Start-Sleep -Milliseconds 500
    }
    if (@(Get-Process -Name $Name -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "Could not establish the closed start state for $Name."
    }
}

function Close-RapidPcVisibleWindows {
    param([Parameter(Mandatory)][string]$Name)

    $visible = @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
    foreach ($process in $visible) {
        [void]$process.CloseMainWindow()
    }
    if ($visible.Count -gt 0) {
        Start-Sleep -Milliseconds 1000
    }
    $remaining = @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
    foreach ($process in $remaining) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
    }
    if ($remaining.Count -gt 0) {
        Start-Sleep -Milliseconds 500
    }
    if (Test-RapidPcVisibleWindow -Name $Name) {
        throw "Could not establish the closed-window start state for $Name."
    }
}

function Test-RapidPcVisibleWindow {
    param([Parameter(Mandatory)][string]$Name)

    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0
}
