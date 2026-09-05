$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$readme = Get-Content -Raw -LiteralPath (Join-Path $root 'README.md')
$repositoryAgents = Get-Content -Raw -LiteralPath (Join-Path $root 'AGENTS.md')
$instructions = Get-Content -Raw -LiteralPath (Join-Path $root 'agent_install\AGENT_INSTALL_INSTRUCTIONS.md')
$entryPoint = Get-Content -Raw -LiteralPath (Join-Path $root 'agent_install\install.ps1')

function Assert-AgentInstallContract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

Assert-AgentInstallContract `
    ($readme -match 'https://github\.com/tntcannon5000/rapid-pc-use' -and $readme -match 'Install this for me') `
    'README must advertise the repository-URL agent-install experience.'
Assert-AgentInstallContract `
    ($repositoryAgents -match 'agent_install/AGENT_INSTALL_INSTRUCTIONS\.md') `
    'Repository AGENTS.md must route explicit install requests to the authoritative instructions.'
Assert-AgentInstallContract `
    ($instructions -match 'git clone --branch main --single-branch') `
    'Agent instructions must clone the canonical main branch explicitly.'
Assert-AgentInstallContract `
    ($instructions -match 'git remote get-url origin' -and
     $instructions -match 'git branch --show-current' -and
     $instructions -match 'git status --short') `
    'Agent instructions must verify repository identity, branch, and cleanliness.'
Assert-AgentInstallContract `
    ($instructions -match 'does not authorize `-EnableFastMode`') `
    'Agent instructions must not infer fast-mode authority from an install request.'
Assert-AgentInstallContract `
    ($instructions -match 'Do not try to use the newly installed plugin in the current task') `
    'Agent instructions must preserve the required desktop restart boundary.'
Assert-AgentInstallContract `
    ($entryPoint.Contains('$installer = Join-Path $root ''scripts\install.ps1''') -and
     $entryPoint.Contains('$parameters = @{ ForceBuild = $true }')) `
    'The public agent entry point must continue delegating to a forced source build.'

Write-Host 'PASS repository URL plus an explicit install request reaches the verified source installer'
