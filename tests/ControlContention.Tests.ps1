$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'

function Assert-Test {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Start-TestDriver {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    [void]$process.Start()
    return $process
}

function Invoke-TestMcp {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][int]$Id,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][hashtable]$Params
    )

    $request = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Params } |
        ConvertTo-Json -Compress -Depth 10
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    $line = $Process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "The contention-test driver returned no response for $Method."
    }
    return $line | ConvertFrom-Json
}

function Stop-TestDriver {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try { $Process.StandardInput.Close() } catch { }
    try {
        if (-not $Process.HasExited -and -not $Process.WaitForExit(2000)) {
            $Process.Kill($true)
            [void]$Process.WaitForExit(5000)
        }
    }
    catch { }
    $Process.Dispose()
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the driver before running contention tests: $executable"
}

$owner = $null
$contender = $null
try {
    $owner = Start-TestDriver
    $contender = Start-TestDriver
    foreach ($process in @($owner, $contender)) {
        $initialize = Invoke-TestMcp -Process $process -Id 1 -Method 'initialize' -Params @{
            protocolVersion = '2025-11-25'
            capabilities = @{}
            clientInfo = @{ name = 'rapid-pc-use-contention-test'; version = '1' }
        }
        Assert-Test ($null -eq $initialize.PSObject.Properties['error']) 'A contention-test driver rejected initialization.'
    }

    $ownerObservation = Invoke-TestMcp -Process $owner -Id 2 -Method 'tools/call' -Params @{
        name = 'pc_observe'
        arguments = @{ begin_control = $true; capture_scope = 'active_window' }
    }
    Assert-Test ($ownerObservation.result.isError -ne $true) 'The first driver could not acquire desktop control.'
    $ownerStatus = [string]$ownerObservation.result.structuredContent.status
    if ($ownerStatus -eq 'blocked' -and
        [string]$ownerObservation.result.structuredContent.code -eq 'desktop_input_blocked') {
        $nativeError = [string]$ownerObservation.result.structuredContent.native_error_code
        throw "Cross-host contention requires a writable interactive desktop, but Windows blocked synthetic input before the owner acquired the lease (desktop_input_blocked, native_error_code=$nativeError)."
    }
    Assert-Test ($ownerStatus -eq 'observing') "The first driver returned unexpected status '$ownerStatus' instead of acquiring desktop control."

    $blocked = Invoke-TestMcp -Process $contender -Id 2 -Method 'tools/call' -Params @{
        name = 'pc_observe'
        arguments = @{ begin_control = $true; capture_scope = 'active_window' }
    }
    Assert-Test ($blocked.result.isError -ne $true) 'Cross-host contention became a terminal driver error.'
    Assert-Test ($blocked.result.structuredContent.status -eq 'blocked') 'Cross-host contention did not return blocked.'
    Assert-Test ($blocked.result.structuredContent.code -eq 'desktop_control_busy') 'Cross-host contention omitted its stable reason code.'
    Assert-Test ($blocked.result.structuredContent.retryable -eq $true) 'Cross-host contention was not marked retryable.'
    Assert-Test ($blocked.result.structuredContent.no_actions_executed -eq $true) 'Cross-host contention did not guarantee zero native input.'

    [void](Invoke-TestMcp -Process $owner -Id 3 -Method 'tools/call' -Params @{ name = 'pc_stop'; arguments = @{} })
    $recovered = Invoke-TestMcp -Process $contender -Id 3 -Method 'tools/call' -Params @{
        name = 'pc_observe'
        arguments = @{ begin_control = $true; capture_scope = 'active_window' }
    }
    Assert-Test ($recovered.result.isError -ne $true) 'The blocked host could not acquire control after release.'
    [void](Invoke-TestMcp -Process $contender -Id 4 -Method 'tools/call' -Params @{ name = 'pc_stop'; arguments = @{} })
}
finally {
    Stop-TestDriver $contender
    Stop-TestDriver $owner
}

Write-Host 'Cross-host desktop control contention tests passed.'
