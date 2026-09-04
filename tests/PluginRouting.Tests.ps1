$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$skillPath = Join-Path $root 'plugin\rapid-pc-use\skills\rapid-pc-use\SKILL.md'
$agentMetadataPath = Join-Path $root 'plugin\rapid-pc-use\skills\rapid-pc-use\agents\openai.yaml'
$installerPath = Join-Path $root 'scripts\install.ps1'

function Assert-RoutingContract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$skill = Get-Content -Raw -LiteralPath $skillPath
$agentMetadata = Get-Content -Raw -LiteralPath $agentMetadataPath
$installer = Get-Content -Raw -LiteralPath $installerPath

Assert-RoutingContract `
    ($agentMetadata -match '(?m)^\s*allow_implicit_invocation:\s*true\s*$') `
    'Rapid PC Use must remain available for precise implicit skill selection.'
Assert-RoutingContract `
    ($skill -match 'Do not use it for work that a shell, filesystem tool, API, connector, or structured browser tool can complete') `
    'The skill description must keep structured execution ahead of GUI automation.'
Assert-RoutingContract `
    ($skill -match 'never merely to type a command into a visible terminal') `
    'The skill description must reject terminal typing as a GUI-use trigger.'
Assert-RoutingContract `
    ($installer -match 'Prefer a dedicated API, connector, filesystem tool, direct shell command, or structured browser automation') `
    'Installed global guidance must keep structured execution ahead of GUI automation.'
Assert-RoutingContract `
    ($installer -match 'never use it just to type a command into a visible terminal') `
    'Installed global guidance must reject terminal typing as a GUI-use trigger.'
Assert-RoutingContract `
    ($installer -match 'give Rapid PC Use only the remaining visual objective') `
    'Installed global guidance must preserve hybrid structured and visual workflows.'

Write-Host 'PASS implicit routing is limited to irreducibly visual Windows work'
