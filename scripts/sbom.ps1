[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root 'dist\rapid-pc-use.spdx.json'
}
$manifestPath = Join-Path $root 'plugin\rapid-pc-use\.codex-plugin\plugin.json'
$version = [string]((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).version)
$version = $version.Split('+')[0]
$files = @(
    [pscustomobject]@{ Path = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'; Relative = 'plugin/rapid-pc-use/bin/win-x64/rapid-pc-use.exe' }
    [pscustomobject]@{ Path = Join-Path $root 'dist\rapid-pc-use-win-x64.zip'; Relative = 'dist/rapid-pc-use-win-x64.zip' }
)
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Path -PathType Leaf)) {
        throw "SBOM input is missing: $($file.Path)"
    }
}

$archiveHash = (Get-FileHash -LiteralPath $files[1].Path -Algorithm SHA256).Hash.ToLowerInvariant()
$documentNamespace = "https://github.com/tntcannon5000/rapid-pc-use/sbom/$version/$archiveHash"
$fileEntries = @()
$relationships = @(
    [ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-Package-RapidPcUse' }
    [ordered]@{ spdxElementId = 'SPDXRef-Package-RapidPcUse'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-Package-DotNetRuntime' }
)
$index = 0
foreach ($file in $files) {
    $index++
    $identifier = "SPDXRef-File-$index"
    $fileEntries += [ordered]@{
        fileName = './' + $file.Relative
        SPDXID = $identifier
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant() })
        copyrightText = 'NOASSERTION'
    }
    $relationships += [ordered]@{ spdxElementId = 'SPDXRef-Package-RapidPcUse'; relationshipType = 'CONTAINS'; relatedSpdxElement = $identifier }
}

$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "rapid-pc-use-$version"
    documentNamespace = $documentNamespace
    creationInfo = [ordered]@{
        created = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: RapidPcUse-SBOM-1.0')
    }
    packages = @(
        [ordered]@{
            name = 'rapid-pc-use'
            SPDXID = 'SPDXRef-Package-RapidPcUse'
            versionInfo = $version
            downloadLocation = 'https://github.com/tntcannon5000/rapid-pc-use'
            filesAnalyzed = $true
            licenseConcluded = 'MIT'
            licenseDeclared = 'MIT'
            copyrightText = 'NOASSERTION'
        }
        [ordered]@{
            name = 'Microsoft .NET Runtime'
            SPDXID = 'SPDXRef-Package-DotNetRuntime'
            versionInfo = '10.0.10'
            downloadLocation = 'https://github.com/dotnet/runtime'
            filesAnalyzed = $false
            licenseConcluded = 'MIT'
            licenseDeclared = 'MIT'
            copyrightText = 'Copyright Microsoft Corporation'
        }
    )
    files = $fileEntries
    relationships = $relationships
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force (Split-Path $resolvedOutput -Parent) | Out-Null
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resolvedOutput, ($sbom | ConvertTo-Json -Depth 12), $utf8NoBom)
Write-Host "Created SPDX SBOM: $resolvedOutput" -ForegroundColor Green
