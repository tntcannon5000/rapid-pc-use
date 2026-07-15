[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkVersion = '10.0.109'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\RapidPcUse\RapidPcUse.csproj'
$plugin = Join-Path $root 'plugin\rapid-pc-use'
$binRoot = [IO.Path]::GetFullPath((Join-Path $plugin 'bin'))
$output = [IO.Path]::GetFullPath((Join-Path $binRoot $Runtime))
$localDotnetRoot = Join-Path $root ".tools\dotnet\$sdkVersion"
$localDotnet = Join-Path $localDotnetRoot 'dotnet.exe'

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
            $installer = Join-Path $env:TEMP "dotnet-install-rapid-pc-use-$installToken.ps1"
            $installParent = Split-Path $localDotnetRoot -Parent
            $installStaging = Join-Path $installParent ".$sdkVersion.staging-$installToken"
            New-Item -ItemType Directory -Force $installParent | Out-Null
            try {
                Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
                & $installer -Version $sdkVersion -InstallDir $installStaging -NoPath
                if ($LASTEXITCODE -ne 0 -or -not (Test-DotnetSdk (Join-Path $installStaging 'dotnet.exe') $sdkVersion)) {
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
                if (Test-Path -LiteralPath $installer) {
                    Remove-Item -LiteralPath $installer -Force
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
