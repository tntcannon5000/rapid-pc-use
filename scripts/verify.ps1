[CmdletBinding()]
param(
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productProject = Join-Path $root 'src\RapidPcUse\RapidPcUse.csproj'
$probeProject = Join-Path $root 'tools\InputApiProbe\InputApiProbe.csproj'
$securityTestsProject = Join-Path $root 'tests\RapidPcUse.SecurityTests\RapidPcUse.SecurityTests.csproj'
$plugin = Join-Path $root 'plugin\rapid-pc-use'
$manifestPath = Join-Path $plugin '.codex-plugin\plugin.json'
$executable = Join-Path $plugin 'bin\win-x64\rapid-pc-use.exe'
$archive = Join-Path $root 'dist\rapid-pc-use-win-x64.zip'
$checksum = "$archive.sha256"

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) {
    throw 'Release build failed.'
}

$sdkVersion = [string]((Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version)
$dotnetCandidates = @(Join-Path $root ".tools\dotnet\$sdkVersion\dotnet.exe")
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $systemDotnet) {
    $dotnetCandidates += $systemDotnet.Source
}
$dotnet = $null
foreach ($candidate in ($dotnetCandidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }
    $candidateSdks = @(& $candidate --list-sdks 2>$null)
    $hasRequiredSdk = @($candidateSdks | Where-Object { $_ -match "^$([Regex]::Escape($sdkVersion))\s" }).Count -gt 0
    if ($LASTEXITCODE -eq 0 -and $hasRequiredSdk) {
        $dotnet = $candidate
        break
    }
}
if ($null -eq $dotnet) {
    throw "The pinned .NET SDK $sdkVersion was not found after the build."
}

foreach ($project in @($productProject, $probeProject, $securityTestsProject)) {
    & $dotnet build $project -c Release --nologo -p:AnalysisLevel=latest-recommended
    if ($LASTEXITCODE -ne 0) {
        throw "Strict analyzer build failed: $project"
    }
}

foreach ($project in @($productProject, $probeProject, $securityTestsProject)) {
    & $dotnet format $project --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Formatting verification failed: $project"
    }
}

& $dotnet run --project $securityTestsProject -c Release --no-build
if ($LASTEXITCODE -ne 0) {
    throw 'Security regression tests failed.'
}

$parseErrors = @()
foreach ($script in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    foreach ($error in @($errors)) {
        $parseErrors += "$($script.Name): $($error.Message)"
    }
}
if ($parseErrors.Count -gt 0) {
    throw "PowerShell syntax validation failed:`n$($parseErrors -join "`n")"
}

$jsonFiles = @(
    Get-Item -LiteralPath (Join-Path $root 'global.json')
    Get-ChildItem -LiteralPath $plugin -Filter '*.json' -File -Recurse
)
foreach ($jsonFile in $jsonFiles) {
    try {
        Get-Content -Raw -LiteralPath $jsonFile.FullName | ConvertFrom-Json | Out-Null
    }
    catch {
        throw "Invalid JSON in $($jsonFile.FullName): $($_.Exception.Message)"
    }
}

$manifestVersion = [string]((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).version)
$manifestVersion = $manifestVersion.Split('+')[0]
$buildInfo = Get-Content -Raw -LiteralPath (Join-Path $root 'src\RapidPcUse\BuildInfo.cs')
$buildInfoMatch = [Regex]::Match($buildInfo, 'Version\s*=\s*"(?<version>[^"]+)"')
if (-not $buildInfoMatch.Success) {
    throw 'Could not read the driver version from BuildInfo.cs.'
}
[xml]$projectXml = Get-Content -Raw -LiteralPath $productProject
$projectVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$fileVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/FileVersion').InnerText
$applicationManifest = Get-Content -Raw -LiteralPath (Join-Path $root 'src\RapidPcUse\app.manifest')
$manifestIdentityMatch = [Regex]::Match($applicationManifest, '<assemblyIdentity\s+version="(?<version>[^"]+)"')
if (-not $manifestIdentityMatch.Success) {
    throw 'Could not read the assembly identity from app.manifest.'
}
$executableVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
$versions = @{
    manifest = $manifestVersion
    driver = $buildInfoMatch.Groups['version'].Value
    project = $projectVersion
}
foreach ($entry in $versions.GetEnumerator()) {
    if ($entry.Value -ne $manifestVersion) {
        throw "Version mismatch: $($entry.Key) is '$($entry.Value)', expected '$manifestVersion'."
    }
}
if ($fileVersion -ne "$manifestVersion.0" -or
    $manifestIdentityMatch.Groups['version'].Value -ne $fileVersion -or
    $executableVersion -ne $fileVersion) {
    throw "Executable version '$executableVersion' does not match project file version '$fileVersion'."
}

foreach ($requiredFile in @(
    $executable,
    (Join-Path $plugin 'DOTNET-LICENSE.txt'),
    (Join-Path $plugin 'DOTNET-THIRD-PARTY-NOTICES.txt')
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required release file is missing: $requiredFile"
    }
}

$smoke = $null
if (-not $SkipSmoke) {
    $smoke = & (Join-Path $PSScriptRoot 'smoke.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Active-control smoke verification failed.'
    }
}

& (Join-Path $PSScriptRoot 'package.ps1') -SkipBuild
if ($LASTEXITCODE -ne 0) {
    throw 'Release packaging failed.'
}
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or -not (Test-Path -LiteralPath $checksum -PathType Leaf)) {
    throw 'The release archive or checksum file was not created.'
}
$expectedHash = ((Get-Content -LiteralPath $checksum -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expectedHash -ne $actualHash) {
    throw "Package checksum mismatch: expected $expectedHash, got $actualHash."
}

$archiveEntries = @(& tar.exe -tf $archive)
if ($LASTEXITCODE -ne 0) {
    throw 'The release archive could not be read.'
}
$requiredEntries = @(
    'CHANGELOG.md',
    'LICENSE',
    'README.md',
    'RELEASING.md',
    'SECURITY.md',
    'plugin/rapid-pc-use/.codex-plugin/plugin.json',
    'plugin/rapid-pc-use/bin/win-x64/rapid-pc-use.exe',
    'plugin/rapid-pc-use/DOTNET-LICENSE.txt',
    'plugin/rapid-pc-use/DOTNET-THIRD-PARTY-NOTICES.txt',
    'scripts/install.ps1',
    'scripts/smoke.ps1'
)
foreach ($entry in $requiredEntries) {
    if ($archiveEntries -notcontains $entry) {
        throw "The release archive is missing '$entry'."
    }
}

& git -C $root diff --check
if ($LASTEXITCODE -ne 0) {
    throw 'The working tree contains whitespace errors.'
}
& git -C $root diff --cached --check
if ($LASTEXITCODE -ne 0) {
    throw 'The staged changes contain whitespace errors.'
}

[pscustomobject]@{
    Version = $manifestVersion
    Sdk = $sdkVersion
    ExecutableMiB = [Math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 1)
    Displays = if ($null -eq $smoke) { 'skipped' } else { $smoke.Displays }
    PackageSha256 = $actualHash
    Result = 'release verification passed'
}
