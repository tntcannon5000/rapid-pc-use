[CmdletBinding()]
param(
    [switch]$EnableFastMode
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Rapid PC Use requires 64-bit Windows.'
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installer = Join-Path $root 'scripts\install.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'The repository is incomplete: scripts\install.ps1 is missing.'
}

Write-Host 'Building Rapid PC Use from this checkout...' -ForegroundColor Cyan
$parameters = @{ ForceBuild = $true }
if ($EnableFastMode) {
    $parameters.EnableFastMode = $true
}

& $installer @parameters

Write-Host 'Installation and hash verification completed.' -ForegroundColor Green
Write-Host 'Restart Codex or ChatGPT desktop before using Rapid PC Use.' -ForegroundColor Yellow
