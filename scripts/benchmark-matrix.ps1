[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Task,
    [Parameter(Mandatory)]
    [switch]$RunLive,
    [string[]]$Models = @('gpt-5.6-luna', 'gpt-5.6-terra', 'gpt-5.6-sol'),
    [string[]]$Reasoning = @('none', 'low'),
    [ValidateSet('720', '900')]
    [string[]]$CaptureTiers = @('720', '900'),
    [int]$MaxDurationMs = 120000
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $root 'plugin\rapid-pc-use\bin\win-x64\rapid-pc-use.exe'

if (-not $RunLive) {
    throw 'Live benchmarking requires the explicit -RunLive switch.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Build the plugin before benchmarking: $executable"
}

$rows = [Collections.Generic.List[object]]::new()
foreach ($model in $Models) {
    foreach ($effort in $Reasoning) {
        foreach ($tier in $CaptureTiers) {
            $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardInput = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $startInfo.Environment['RAPID_PC_AGENT_ENABLED'] = '1'
            $startInfo.Environment['RAPID_PC_AGENT_PROVIDER'] = 'codex'
            $startInfo.Environment['RAPID_PC_AGENT_MODEL'] = $model
            $startInfo.Environment['RAPID_PC_AGENT_REASONING'] = $effort
            $startInfo.Environment['RAPID_PC_AGENT_SERVICE_TIER'] = 'fast'
            $startInfo.Environment['RAPID_PC_AGENT_IMAGE_DETAIL'] = 'original'
            $startInfo.Environment['RAPID_PC_CAPTURE_TIER'] = $tier

            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            $started = $false
            try {
                [void]$process.Start()
                $started = $true
                $request = @{
                    jsonrpc = '2.0'
                    id = 1
                    method = 'tools/call'
                    params = @{
                        name = 'pc_run'
                        arguments = @{
                            task = $Task
                            limits = @{ max_duration_ms = $MaxDurationMs }
                        }
                    }
                } | ConvertTo-Json -Compress -Depth 8
                $process.StandardInput.WriteLine($request)
                $process.StandardInput.Close()
                $line = $process.StandardOutput.ReadLine()
                if ([string]::IsNullOrWhiteSpace($line)) {
                    throw "The benchmark driver returned no MCP response. $($process.StandardError.ReadToEnd())"
                }

                $response = $line | ConvertFrom-Json
                $result = $response.result.structuredContent
                $rows.Add([pscustomobject]@{
                    Model = $model
                    Reasoning = $effort
                    CaptureTier = $tier
                    Status = [string]$result.status
                    ModelTurns = [int]$result.model_turns
                    Actions = [int]$result.actions_executed
                    ElapsedMs = [long]$result.elapsed_ms
                    ActionsPerSecond = if ($result.elapsed_ms -gt 0) {
                        [Math]::Round(1000 * [double]$result.actions_executed / [double]$result.elapsed_ms, 3)
                    }
                    else { $null }
                })
            }
            finally {
                if ($started -and -not $process.HasExited) {
                    $process.Kill($true)
                }
                $process.Dispose()
            }
        }
    }
}

$rows | Sort-Object ElapsedMs | Format-Table -AutoSize
