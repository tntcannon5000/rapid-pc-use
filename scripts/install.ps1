[CmdletBinding()]
param(
    [switch]$ForceBuild,
    [switch]$SkipGlobalGuidance,
    [switch]$EnableFastMode
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePlugin = Join-Path $root 'plugin\rapid-pc-use'
$sourceExe = Join-Path $sourcePlugin 'bin\win-x64\rapid-pc-use.exe'

if ($ForceBuild -or -not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "Published driver not found: $sourceExe"
}
$sourceExeHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$sourceSignature = Get-AuthenticodeSignature -LiteralPath $sourceExe
if ($sourceSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    Write-Warning 'This public-beta executable is not Authenticode-signed. Verify its SHA-256 and GitHub provenance before installation.'
}

$pluginParent = [IO.Path]::GetFullPath((Join-Path $HOME 'plugins'))
$destination = [IO.Path]::GetFullPath((Join-Path $pluginParent 'rapid-pc-use'))
if (-not $destination.StartsWith($pluginParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to install outside the personal plugin directory.'
}

New-Item -ItemType Directory -Force $pluginParent | Out-Null
$token = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $pluginParent ".rapid-pc-use.staging-$token"
$backup = Join-Path $pluginParent ".rapid-pc-use.backup-$token"

try {
    Copy-Item -LiteralPath $sourcePlugin -Destination $staging -Recurse -Force
    if (Test-Path -LiteralPath $destination) {
        Move-Item -LiteralPath $destination -Destination $backup
    }
    Move-Item -LiteralPath $staging -Destination $destination
    if (Test-Path -LiteralPath $backup) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
}
catch {
    if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $destination
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

# A unique build-metadata suffix makes the desktop refresh its cached local copy.
$installTimestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$installedManifestPath = Join-Path $destination '.codex-plugin\plugin.json'
$installedManifest = Get-Content -Raw -LiteralPath $installedManifestPath | ConvertFrom-Json
$baseVersion = ([string]$installedManifest.version).Split('+')[0]
$installedManifest.version = "$baseVersion+codex.local-$installTimestamp"
$manifestJson = $installedManifest | ConvertTo-Json -Depth 12

$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($installedManifestPath, $manifestJson, $utf8NoBom)
$installedExe = Join-Path $destination 'bin\win-x64\rapid-pc-use.exe'
$installedExeHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
if ($installedExeHash -ne $sourceExeHash) {
    throw 'The installed driver hash does not match the verified build artifact.'
}
$installedSignature = Get-AuthenticodeSignature -LiteralPath $installedExe
if ($sourceSignature.Status -eq [Management.Automation.SignatureStatus]::Valid -and
    $installedSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    throw 'The installed driver lost its valid Authenticode signature.'
}
$marketplacePath = Join-Path $HOME '.agents\plugins\marketplace.json'
New-Item -ItemType Directory -Force (Split-Path $marketplacePath -Parent) | Out-Null
if (Test-Path -LiteralPath $marketplacePath) {
    $marketplace = Get-Content -Raw -LiteralPath $marketplacePath | ConvertFrom-Json
    if ($marketplace.name -ne 'personal') {
        throw "The existing personal marketplace uses the unexpected name '$($marketplace.name)'."
    }
}
else {
    $marketplace = [pscustomobject]@{
        name = 'personal'
        interface = [pscustomobject]@{ displayName = 'Personal' }
        plugins = @()
    }
}

$entry = [pscustomobject]@{
    name = 'rapid-pc-use'
    source = [pscustomobject]@{ source = 'local'; path = './plugins/rapid-pc-use' }
    policy = [pscustomobject]@{ installation = 'INSTALLED_BY_DEFAULT'; authentication = 'ON_INSTALL' }
    category = 'Developer Tools'
}
$existing = @($marketplace.plugins | Where-Object { $_.name -ne 'rapid-pc-use' })
$marketplace.plugins = @($existing) + $entry
[IO.File]::WriteAllText($marketplacePath, ($marketplace | ConvertTo-Json -Depth 12), $utf8NoBom)

# Prefer the Codex binary bundled with the desktop app. A separately installed
# PATH CLI can be older and may not implement plugin commands yet.
$codexCandidates = @()
$desktopBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
if (Test-Path -LiteralPath $desktopBin) {
    $codexCandidates += Get-ChildItem -LiteralPath $desktopBin -Recurse -Filter 'codex.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -ExpandProperty FullName
}
$pathCodex = Get-Command codex -ErrorAction SilentlyContinue
if ($null -ne $pathCodex) {
    $codexCandidates += $pathCodex.Source
}

$pluginCli = $null
foreach ($candidate in ($codexCandidates | Select-Object -Unique)) {
    & $candidate plugin --help *> $null
    if ($LASTEXITCODE -eq 0) {
        $pluginCli = $candidate
        break
    }
}

if ($null -eq $pluginCli) {
    throw 'No installed Codex CLI supports plugin installation. Update the ChatGPT desktop app and rerun this installer.'
}

& $pluginCli plugin add 'rapid-pc-use@personal'
if ($LASTEXITCODE -ne 0) {
    throw 'Codex discovered the personal marketplace but failed to install rapid-pc-use.'
}

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
New-Item -ItemType Directory -Force $codexHome | Out-Null
$configPath = Join-Path $codexHome 'config.toml'
if (-not (Test-Path -LiteralPath $configPath)) {
    [IO.File]::WriteAllText($configPath, '', $utf8NoBom)
}
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item -LiteralPath $configPath -Destination "$configPath.rapid-pc-use.$timestamp.bak" -Force
$config = [IO.File]::ReadAllText($configPath)

function Set-TomlSection([string]$Text, [string]$Header, [string[]]$Lines) {
    $body = $Header + "`r`n" + ($Lines -join "`r`n") + "`r`n"
    $pattern = '(?ms)^' + [Regex]::Escape($Header) + '\s*\r?\n.*?(?=^\[|\z)'
    if ([Regex]::IsMatch($Text, $pattern)) {
        return [Regex]::Replace($Text, $pattern, $body, 1)
    }
    return $Text.TrimEnd() + "`r`n`r`n" + $body
}

$config = Set-TomlSection $config '[plugins."rapid-pc-use@personal"]' @('enabled = true')
$approvalMode = if ($EnableFastMode) { 'approve' } else { 'prompt' }
$config = Set-TomlSection $config '[plugins."rapid-pc-use@personal".mcp_servers.rapid_pc_use]' @(
    'enabled = true',
    "default_tools_approval_mode = `"$approvalMode`""
)
[IO.File]::WriteAllText($configPath, $config, $utf8NoBom)

if (-not $SkipGlobalGuidance) {
    $agentsPath = Join-Path $codexHome 'AGENTS.md'
    $startMarker = '<!-- RAPID-PC-USE:START -->'
    $endMarker = '<!-- RAPID-PC-USE:END -->'
    $block = @'
<!-- RAPID-PC-USE:START -->
## Rapid PC Use

When the user asks to operate the visible Windows desktop or any GUI app, use the implicitly available `rapid-pc-use` skill and its `pc_observe`/`pc_act`/`pc_stop` tools. Prefer it over built-in Computer Use for full-desktop work and keep the visual loop fast in the main task. On `RAPID_PC_USE_FAILURE` or a driver transport failure, stop the PC task immediately: make no retry or workaround calls, perform only the skill's single bounded read-only log inspection, and give the user a 1-3 sentence plain-language summary without a deep investigation. If the driver reports "The user is now operating the PC", make no further automation or log-inspection calls and end the turn immediately.
<!-- RAPID-PC-USE:END -->
'@
    $agents = if (Test-Path -LiteralPath $agentsPath) { [IO.File]::ReadAllText($agentsPath) } else { '' }
    if (Test-Path -LiteralPath $agentsPath) {
        Copy-Item -LiteralPath $agentsPath -Destination "$agentsPath.rapid-pc-use.$timestamp.bak" -Force
    }
    $pattern = '(?ms)' + [Regex]::Escape($startMarker) + '.*?' + [Regex]::Escape($endMarker)
    if ([Regex]::IsMatch($agents, $pattern)) {
        $agents = [Regex]::Replace($agents, $pattern, $block, 1)
    }
    else {
        $agents = $agents.TrimEnd() + "`r`n`r`n" + $block + "`r`n"
    }
    [IO.File]::WriteAllText($agentsPath, $agents, $utf8NoBom)
}

Write-Host 'Rapid PC Use is installed and enabled.' -ForegroundColor Green
Write-Host "Plugin: $destination"
Write-Host "Marketplace: $marketplacePath"
Write-Host "Driver SHA-256: $installedExeHash"
if ($EnableFastMode) {
    Write-Warning 'Fast mode was explicitly enabled. Native PC actions will not prompt for per-call approval.'
}
Write-Host 'Restart the ChatGPT desktop app, then open a new task and ask it to operate your PC.' -ForegroundColor Yellow
