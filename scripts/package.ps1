[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$plugin = Join-Path $root 'plugin\rapid-pc-use'
$exe = Join-Path $plugin 'bin\win-x64\rapid-pc-use.exe'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'rapid-pc-use-win-x64'
$archive = Join-Path $dist 'rapid-pc-use-win-x64.zip'

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

$resolvedStage = [IO.Path]::GetFullPath($stage)
$resolvedDist = [IO.Path]::GetFullPath($dist)
if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to stage outside the distribution directory.'
}

New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $stage 'plugin') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stage 'scripts') | Out-Null
Copy-Item -LiteralPath $plugin -Destination (Join-Path $stage 'plugin\rapid-pc-use') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'scripts\install.ps1') -Destination (Join-Path $stage 'scripts\install.ps1')
Copy-Item -LiteralPath (Join-Path $root 'scripts\smoke.ps1') -Destination (Join-Path $stage 'scripts\smoke.ps1')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE')

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Push-Location $stage
try {
    & tar.exe -a -c -f $archive '*'
    if ($LASTEXITCODE -ne 0) {
        throw 'Archive creation failed.'
    }
}
finally {
    Pop-Location
}
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Packaged Rapid PC Use: $archive" -ForegroundColor Green
