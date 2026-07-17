[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkVersion = '10.0.110'
$sdkArchiveSha512 = '652eaabac68508925225ca4b3a4f7be0353ec69c40bff838a05709769e94b9013f8bf03ee396d0cabe8438508a83521b2ced1b23d3a3019b13c49d1feaf6b039'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\RapidPcUse\RapidPcUse.csproj'
$plugin = Join-Path $root 'plugin\rapid-pc-use'
$binRoot = [IO.Path]::GetFullPath((Join-Path $plugin 'bin'))
$output = [IO.Path]::GetFullPath((Join-Path $binRoot $Runtime))
$localDotnetRoot = Join-Path $root ".tools\dotnet\$sdkVersion"
$localDotnet = Join-Path $localDotnetRoot 'dotnet.exe'
$buildMutex = [Threading.Mutex]::new($false, "Local\RapidPcUse.Build.$Runtime")
$buildMutexHeld = $false

try {
    try {
        $buildMutexHeld = $buildMutex.WaitOne([TimeSpan]::FromMinutes(15))
    }
    catch [Threading.AbandonedMutexException] {
        $buildMutexHeld = $true
    }
    if (-not $buildMutexHeld) {
        throw 'Timed out waiting for another Rapid PC Use build to finish.'
    }

function Test-DotnetSdk([string]$Executable, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        return $false
    }

    $sdks = & $Executable --list-sdks 2>$null
    return $LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match "^$([Regex]::Escape($Version))\s" }).Count -gt 0
}

$dotnet = $null
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $systemDotnet -and (Test-DotnetSdk $systemDotnet.Source $sdkVersion)) {
    $dotnet = $systemDotnet.Source
}
elseif (Test-DotnetSdk $localDotnet $sdkVersion) {
    $dotnet = $localDotnet
}
else {
    $installMutex = [Threading.Mutex]::new($false, "Local\RapidPcUse.DotnetSdk.$sdkVersion")
    $installMutexHeld = $false
    try {
        try {
            $installMutexHeld = $installMutex.WaitOne([TimeSpan]::FromMinutes(10))
        }
        catch [Threading.AbandonedMutexException] {
            $installMutexHeld = $true
        }
        if (-not $installMutexHeld) {
            throw "Timed out waiting for another .NET $sdkVersion SDK installation to finish."
        }

        if (Test-DotnetSdk $localDotnet $sdkVersion) {
            $dotnet = $localDotnet
        }
        else {
            $installToken = [Guid]::NewGuid().ToString('N')
            $sdkArchive = Join-Path $env:TEMP "dotnet-sdk-$sdkVersion-win-x64-$installToken.zip"
            $installParent = Split-Path $localDotnetRoot -Parent
            $installStaging = Join-Path $installParent ".$sdkVersion.staging-$installToken"
            New-Item -ItemType Directory -Force $installParent | Out-Null
            try {
                $sdkUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip"
                Invoke-WebRequest -UseBasicParsing $sdkUrl -OutFile $sdkArchive
                $actualArchiveHash = (Get-FileHash -LiteralPath $sdkArchive -Algorithm SHA512).Hash.ToLowerInvariant()
                if ($actualArchiveHash -ne $sdkArchiveSha512) {
                    throw "The .NET SDK archive hash '$actualArchiveHash' does not match the pinned Microsoft SHA-512."
                }
                Expand-Archive -LiteralPath $sdkArchive -DestinationPath $installStaging
                if (-not (Test-DotnetSdk (Join-Path $installStaging 'dotnet.exe') $sdkVersion)) {
                    throw "The .NET $sdkVersion SDK installation failed."
                }

                if (Test-Path -LiteralPath $localDotnetRoot) {
                    Remove-Item -LiteralPath $localDotnetRoot -Recurse -Force
                }
                Move-Item -LiteralPath $installStaging -Destination $localDotnetRoot
            }
            finally {
                if (Test-Path -LiteralPath $installStaging) {
                    Remove-Item -LiteralPath $installStaging -Recurse -Force
                }
                if (Test-Path -LiteralPath $sdkArchive) {
                    Remove-Item -LiteralPath $sdkArchive -Force
                }
            }
            $dotnet = $localDotnet
        }
    }
    finally {
        if ($installMutexHeld) {
            $installMutex.ReleaseMutex()
        }
        $installMutex.Dispose()
    }
}

if (-not (Test-DotnetSdk $dotnet $sdkVersion)) {
    throw "The required .NET SDK $sdkVersion is unavailable."
}

$dotnetRoot = Split-Path $dotnet -Parent
$redistributionFiles = @{
    (Join-Path $dotnetRoot 'LICENSE.txt') = (Join-Path $plugin 'DOTNET-LICENSE.txt')
    (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') = (Join-Path $plugin 'DOTNET-THIRD-PARTY-NOTICES.txt')
}
foreach ($entry in $redistributionFiles.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Key -PathType Leaf)) {
        throw "Required .NET redistribution notice not found: $($entry.Key)"
    }
}

New-Item -ItemType Directory -Force $binRoot | Out-Null
$staleStaging = @(Get-ChildItem -LiteralPath $binRoot -Directory -Force -Filter ".$Runtime.staging-*")
$staleBackups = @(Get-ChildItem -LiteralPath $binRoot -Directory -Force -Filter ".$Runtime.backup-*" | Sort-Object LastWriteTimeUtc -Descending)
if (-not (Test-Path -LiteralPath $output) -and $staleBackups.Count -gt 0) {
    Move-Item -LiteralPath $staleBackups[0].FullName -Destination $output
    $staleBackups = @($staleBackups | Select-Object -Skip 1)
}
foreach ($abandoned in @($staleStaging) + @($staleBackups)) {
    $abandonedPath = [IO.Path]::GetFullPath($abandoned.FullName)
    if (-not $abandonedPath.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an abandoned build directory outside the plugin bin directory: $abandonedPath"
    }
    Remove-Item -LiteralPath $abandonedPath -Recurse -Force
}

$token = [Guid]::NewGuid().ToString('N')
$staging = [IO.Path]::GetFullPath((Join-Path $binRoot ".$Runtime.staging-$token"))
$backup = [IO.Path]::GetFullPath((Join-Path $binRoot ".$Runtime.backup-$token"))
foreach ($path in @($output, $staging, $backup)) {
    if (-not $path.StartsWith($binRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to publish outside the plugin binary directory: $path"
    }
}

$swapComplete = $false
try {
    & $dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $staging

    if ($LASTEXITCODE -ne 0) {
        throw 'Rapid PC Use publish failed.'
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $staging -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'rapid-pc-use.exe') {
        throw "Expected one self-contained rapid-pc-use.exe, found: $($publishedFiles.Name -join ', ')"
    }

    if (Test-Path -LiteralPath $output) {
        Move-Item -LiteralPath $output -Destination $backup
    }
    Move-Item -LiteralPath $staging -Destination $output
    $swapComplete = $true
}
catch {
    if (-not $swapComplete -and -not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $output
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

if (Test-Path -LiteralPath $backup) {
    Remove-Item -LiteralPath $backup -Recurse -Force
}
foreach ($entry in $redistributionFiles.GetEnumerator()) {
    Copy-Item -LiteralPath $entry.Key -Destination $entry.Value -Force
}

Write-Host "Built Rapid PC Use: $output" -ForegroundColor Green
}
finally {
    if ($buildMutexHeld) {
        $buildMutex.ReleaseMutex()
    }
    $buildMutex.Dispose()
}
