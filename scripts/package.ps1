[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$plugin = Join-Path $root 'plugin\rapid-pc-use'
$exe = Join-Path $plugin 'bin\win-x64\rapid-pc-use.exe'
$dist = Join-Path $root 'dist'
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $dist ".rapid-pc-use-win-x64.staging-$token"
$archive = Join-Path $dist 'rapid-pc-use-win-x64.zip'
$checksum = "$archive.sha256"
$archiveStaging = Join-Path $dist ".rapid-pc-use-win-x64.$token.zip"
$checksumStaging = "$archiveStaging.sha256"

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Rapid PC Use build failed before packaging.'
    }
}
elseif (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Published driver not found: $exe"
}

$resolvedStage = [IO.Path]::GetFullPath($stage)
$resolvedDist = [IO.Path]::GetFullPath($dist)
if (-not $resolvedStage.StartsWith($resolvedDist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to stage outside the distribution directory.'
}

New-Item -ItemType Directory -Force $dist | Out-Null
try {
    New-Item -ItemType Directory -Force (Join-Path $stage 'plugin') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $stage 'scripts') | Out-Null
    Copy-Item -LiteralPath $plugin -Destination (Join-Path $stage 'plugin\rapid-pc-use') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'scripts\install.ps1') -Destination (Join-Path $stage 'scripts\install.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'scripts\smoke.ps1') -Destination (Join-Path $stage 'scripts\smoke.ps1')
    foreach ($document in @('README.md', 'CHANGELOG.md', 'SECURITY.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination (Join-Path $stage $document)
    }

    Push-Location $stage
    try {
        & tar.exe -a -c -f $archiveStaging '*'
        if ($LASTEXITCODE -ne 0) {
            throw 'Archive creation failed.'
        }
    }
    finally {
        Pop-Location
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archiveStaging).Hash.ToLowerInvariant()
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($checksumStaging, "$hash  $(Split-Path $archive -Leaf)`n", $utf8NoBom)

    foreach ($path in @($archive, $checksum)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    Move-Item -LiteralPath $archiveStaging -Destination $archive
    Move-Item -LiteralPath $checksumStaging -Destination $checksum
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    foreach ($path in @($archiveStaging, $checksumStaging)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}
Write-Host "Packaged Rapid PC Use: $archive" -ForegroundColor Green
Write-Host "SHA-256: $checksum" -ForegroundColor Green
