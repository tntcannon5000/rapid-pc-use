[CmdletBinding()]
param(
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) {
    $Executable = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'
}
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Driver executable not found: $Executable"
}

$start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
$start.WorkingDirectory = Split-Path $start.FileName
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
[void]$process.Start()

function Invoke-Mcp([int]$Id, [string]$Method, [hashtable]$Parameters) {
    $request = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $process.StandardInput.WriteLine($request)
    $line = $process.StandardOutput.ReadLine()
    if (-not $line) {
        throw 'The driver closed its output before returning an MCP response.'
    }
    return $line | ConvertFrom-Json
}

try {
    $initialize = Invoke-Mcp 1 'initialize' @{ protocolVersion = '2025-11-25' }
    $observe = Invoke-Mcp 2 'tools/call' @{
        name = 'pc_observe'
        arguments = @{ begin_control = $true }
    }
    $stop = Invoke-Mcp 3 'tools/call' @{ name = 'pc_stop'; arguments = @{} }
}
finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) {
        $process.Kill($true)
        throw 'The driver did not exit within five seconds after stdin closed.'
    }
}

if ($observe.result.isError) {
    throw "Active observation failed: $((@($observe.result.content | Where-Object type -eq 'text').text) -join ' ')"
}
if ($stop.result.isError) {
    throw "Control stop failed: $((@($stop.result.content | Where-Object type -eq 'text').text) -join ' ')"
}

$manifestText = [string]($observe.result.content | Where-Object type -eq 'text' | Select-Object -First 1 -ExpandProperty text)
$manifestMatch = [Regex]::Match($manifestText, '^RAPID_PC_FRAME (?<json>.+)$')
$displayCount = if ($manifestMatch.Success) {
    (($manifestMatch.Groups['json'].Value | ConvertFrom-Json).displays).Count
} else {
    0
}

[pscustomobject]@{
    Server = $initialize.result.serverInfo.name
    Version = $initialize.result.serverInfo.version
    Displays = $displayCount
    ActiveObserve = 'passed'
    Stop = 'passed'
    ProcessExit = $process.ExitCode
}
