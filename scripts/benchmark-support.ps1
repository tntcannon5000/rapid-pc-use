function New-RapidPcBenchmarkSchedule {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Models,
        [Parameter(Mandatory)]
        [string[]]$Reasoning,
        [Parameter(Mandatory)]
        [string[]]$CaptureTiers,
        [ValidateRange(1, 100)]
        [int]$Repetitions,
        [int]$RandomSeed
    )

    if ($Models.Count -eq 0 -or $Reasoning.Count -eq 0 -or $CaptureTiers.Count -eq 0) {
        throw 'Every benchmark dimension must contain at least one value.'
    }

    $random = [Random]::new($RandomSeed)
    $schedule = [Collections.Generic.List[object]]::new()
    $runNumber = 0
    for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
        $round = [Collections.Generic.List[object]]::new()
        foreach ($model in $Models) {
            foreach ($effort in $Reasoning) {
                foreach ($tier in $CaptureTiers) {
                    $round.Add([pscustomobject]@{
                        Repetition = $repetition
                        Model = $model
                        Reasoning = $effort
                        CaptureTier = $tier
                    })
                }
            }
        }

        for ($index = $round.Count - 1; $index -gt 0; $index--) {
            $swapIndex = $random.Next($index + 1)
            $temporary = $round[$index]
            $round[$index] = $round[$swapIndex]
            $round[$swapIndex] = $temporary
        }

        foreach ($configuration in $round) {
            $runNumber++
            $schedule.Add([pscustomobject]@{
                RunNumber = $runNumber
                Repetition = $configuration.Repetition
                Model = $configuration.Model
                Reasoning = $configuration.Reasoning
                CaptureTier = $configuration.CaptureTier
            })
        }
    }

    return @($schedule)
}

function ConvertFrom-RapidPcFixtureVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Json
    )

    try {
        $verification = $Json | ConvertFrom-Json
    }
    catch {
        throw 'The fixture verifier must emit exactly one JSON object.'
    }

    $successProperty = $verification.PSObject.Properties['success']
    if ($null -eq $successProperty -or $successProperty.Value -isnot [bool]) {
        throw "The fixture verifier JSON must contain a Boolean 'success'."
    }

    $usefulActionsProperty = $verification.PSObject.Properties['usefulActions']
    $usefulActions = 0L
    if ($null -eq $usefulActionsProperty -or
        -not [long]::TryParse(
            [string]$usefulActionsProperty.Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$usefulActions) -or
        $usefulActions -lt 0 -or
        $usefulActions -gt 1000000) {
        throw "The fixture verifier JSON must contain 'usefulActions' between 0 and 1000000."
    }

    $failureCategory = $null
    $failureProperty = $verification.PSObject.Properties['failureCategory']
    if ($null -ne $failureProperty -and $null -ne $failureProperty.Value) {
        $failureCategory = [string]$failureProperty.Value
        if ($failureCategory -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw "The fixture verifier 'failureCategory' must be a bounded identifier, not free-form text."
        }
    }

    if (-not $successProperty.Value -and [string]::IsNullOrWhiteSpace($failureCategory)) {
        throw "A failed fixture verification must include a bounded 'failureCategory'."
    }

    $errorActions = $null
    $errorActionsProperty = $verification.PSObject.Properties['errorActions']
    if ($null -ne $errorActionsProperty -and $null -ne $errorActionsProperty.Value) {
        $parsedErrorActions = 0L
        if (-not [long]::TryParse([string]$errorActionsProperty.Value, [ref]$parsedErrorActions) -or
            $parsedErrorActions -lt 0 -or $parsedErrorActions -gt 1000000) {
            throw "Fixture verifier 'errorActions' must be between 0 and 1000000."
        }
        $errorActions = $parsedErrorActions
    }

    $fixtureElapsedMs = $null
    $fixtureElapsedProperty = $verification.PSObject.Properties['fixtureElapsedMs']
    if ($null -ne $fixtureElapsedProperty -and $null -ne $fixtureElapsedProperty.Value) {
        $parsedFixtureElapsedMs = 0L
        if (-not [long]::TryParse([string]$fixtureElapsedProperty.Value, [ref]$parsedFixtureElapsedMs) -or
            $parsedFixtureElapsedMs -lt 0 -or $parsedFixtureElapsedMs -gt 3600000) {
            throw "Fixture verifier 'fixtureElapsedMs' must be between 0 and 3600000."
        }
        $fixtureElapsedMs = $parsedFixtureElapsedMs
    }

    return [pscustomobject]@{
        Success = [bool]$successProperty.Value
        UsefulActions = $usefulActions
        ErrorActions = $errorActions
        FixtureElapsedMs = $fixtureElapsedMs
        FailureCategory = $failureCategory
    }
}

function ConvertFrom-RapidPcRunResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Result
    )

    foreach ($requiredProperty in @('status', 'sessionId', 'modelTurns', 'actionsExecuted', 'elapsedMs')) {
        if ($null -eq $Result.PSObject.Properties[$requiredProperty]) {
            throw "The pc_run result omitted required property '$requiredProperty'."
        }
    }

    $status = [string]$Result.status
    if ($status -notin @('completed', 'needs_confirmation', 'needs_handoff', 'blocked', 'limit_reached', 'failed', 'denied', 'user_takeover')) {
        throw 'The pc_run result returned an unknown status.'
    }
    $agentRunId = [string]$Result.sessionId
    if ($agentRunId -notmatch '^run-[A-Fa-f0-9]{12,32}$') {
        throw 'The pc_run result returned an invalid run identifier.'
    }
    $modelTurns = 0
    $actionObjects = 0
    $elapsedMs = 0L
    if (-not [int]::TryParse([string]$Result.modelTurns, [ref]$modelTurns) -or $modelTurns -lt 0 -or $modelTurns -gt 50) {
        throw 'The pc_run result returned an invalid model-turn count.'
    }
    if (-not [int]::TryParse([string]$Result.actionsExecuted, [ref]$actionObjects) -or $actionObjects -lt 0 -or $actionObjects -gt 256) {
        throw 'The pc_run result returned an invalid action-object count.'
    }
    if (-not [long]::TryParse([string]$Result.elapsedMs, [ref]$elapsedMs) -or $elapsedMs -lt 0 -or $elapsedMs -gt 300000) {
        throw 'The pc_run result returned an invalid elapsed time.'
    }

    return [pscustomobject]@{
        Status = $status
        AgentRunId = $agentRunId
        ModelTurns = $modelTurns
        ActionObjects = $actionObjects
        ElapsedMs = $elapsedMs
    }
}

function Get-RapidPcBenchmarkPercentile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [double[]]$Values,
        [Parameter(Mandatory)]
        [ValidateRange(0.0, 1.0)]
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    $ordered = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1)
    return [double]$ordered[$index]
}

function New-RapidPcBenchmarkSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Rows
    )

    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($group in @($Rows | Group-Object Model, Reasoning, CaptureTier)) {
        $runs = @($group.Group)
        $first = $runs[0]
        $elapsedValues = @($runs | ForEach-Object { [double]$_.ElapsedMs })
        $turnValues = @($runs | ForEach-Object { [double]$_.ModelTurns })
        $actionValues = @($runs | ForEach-Object { [double]$_.ActionObjects })
        $verifiedRuns = @($runs | Where-Object {
            $property = $_.PSObject.Properties['FixtureSuccess']
            $null -ne $property -and $null -ne $property.Value
        })
        $successfulRuns = @($verifiedRuns | Where-Object { $_.FixtureSuccess })
        $verifiedElapsed = [double](($verifiedRuns | Measure-Object ElapsedMs -Sum).Sum)
        $successfulActions = [double](($verifiedRuns | Measure-Object UsefulActions -Sum).Sum)
        $errorActionValues = @($verifiedRuns | Where-Object { $null -ne $_.ErrorActions } | ForEach-Object { [double]$_.ErrorActions })
        $totalTurns = [double](($turnValues | Measure-Object -Sum).Sum)
        $totalActions = [double](($actionValues | Measure-Object -Sum).Sum)

        $modelDecisionValues = @($runs | Where-Object { $null -ne $_.TotalModelDecisionMs } | ForEach-Object { [double]$_.TotalModelDecisionMs })
        $requestBuildValues = @($runs | Where-Object { $null -ne $_.TotalRequestBuildMs } | ForEach-Object { [double]$_.TotalRequestBuildMs })
        $parseValues = @($runs | Where-Object { $null -ne $_.TotalDecisionParseMs } | ForEach-Object { [double]$_.TotalDecisionParseMs })
        $policyValues = @($runs | Where-Object { $null -ne $_.TotalPolicyEvaluationMs } | ForEach-Object { [double]$_.TotalPolicyEvaluationMs })
        $completionGuardValues = @($runs | Where-Object { $null -ne $_.TotalCompletionGuardMs } | ForEach-Object { [double]$_.TotalCompletionGuardMs })
        $loopPreparationValues = @($runs | Where-Object { $null -ne $_.TotalLoopPreparationMs } | ForEach-Object { [double]$_.TotalLoopPreparationMs })
        $providerWallValues = @($runs | Where-Object { $null -ne $_.TotalProviderWallMs } | ForEach-Object { [double]$_.TotalProviderWallMs })
        $decisionRouteValues = @($runs | Where-Object { $null -ne $_.TotalDecisionRouteMs } | ForEach-Object { [double]$_.TotalDecisionRouteMs })
        $imageStageValues = @($runs | Where-Object { $null -ne $_.TotalProviderImageStageMs } | ForEach-Object { [double]$_.TotalProviderImageStageMs })
        $sessionSetupValues = @($runs | Where-Object { $null -ne $_.TotalProviderSessionSetupMs } | ForEach-Object { [double]$_.TotalProviderSessionSetupMs })
        $fixtureVerifyValues = @($runs | Where-Object { $null -ne $_.FixtureVerifyWallMs } | ForEach-Object { [double]$_.FixtureVerifyWallMs })
        $captureValues = @($runs | Where-Object { $null -ne $_.TotalCaptureMs } | ForEach-Object { [double]$_.TotalCaptureMs })
        $initialCaptureValues = @($runs | Where-Object { $null -ne $_.InitialCaptureMs } | ForEach-Object { [double]$_.InitialCaptureMs })
        $initialEncodeValues = @($runs | Where-Object { $null -ne $_.InitialCaptureEncodeMs } | ForEach-Object { [double]$_.InitialCaptureEncodeMs })
        $settleValues = @($runs | Where-Object { $null -ne $_.TotalSettleMs } | ForEach-Object { [double]$_.TotalSettleMs })
        $actionExecutionValues = @($runs | Where-Object { $null -ne $_.TotalActionExecutionMs } | ForEach-Object { [double]$_.TotalActionExecutionMs })
        $firstDecisionValues = @($runs | Where-Object { $null -ne $_.MeanFirstDecisionDeltaMs } | ForEach-Object { [double]$_.MeanFirstDecisionDeltaMs })
        $outputFillValues = @($runs | Where-Object { $null -ne $_.MeanOutputFillMs } | ForEach-Object { [double]$_.MeanOutputFillMs })
        $completionGuardCheckValues = @($runs | ForEach-Object {
            $property = $_.PSObject.Properties['CompletionGuardChecks']
            if ($null -ne $property -and $null -ne $property.Value) {
                [int]$property.Value
            }
        })
        $completionGuardMatchValues = @($runs | ForEach-Object {
            $property = $_.PSObject.Properties['CompletionGuardMatches']
            if ($null -ne $property -and $null -ne $property.Value) {
                [int]$property.Value
            }
        })

        $summaries.Add([pscustomobject]@{
            Model = [string]$first.Model
            Reasoning = [string]$first.Reasoning
            CaptureTier = [string]$first.CaptureTier
            Runs = $runs.Count
            VerifiedRuns = $verifiedRuns.Count
            SuccessfulRuns = $successfulRuns.Count
            SuccessRatePercent = if ($verifiedRuns.Count -eq 0) { $null } else {
                [Math]::Round(100 * [double]$successfulRuns.Count / $verifiedRuns.Count, 1)
            }
            SuccessfulActionsPerMinute = if ($verifiedElapsed -le 0) { $null } else {
                [Math]::Round(60000 * $successfulActions / $verifiedElapsed, 2)
            }
            MeanErrorActions = if ($errorActionValues.Count -eq 0) { $null } else {
                [Math]::Round([double](($errorActionValues | Measure-Object -Average).Average), 3)
            }
            ActionObjectsPerModelTurn = if ($totalTurns -le 0) { $null } else {
                [Math]::Round($totalActions / $totalTurns, 3)
            }
            P50ElapsedMs = [Math]::Round((Get-RapidPcBenchmarkPercentile $elapsedValues 0.50), 3)
            P95ElapsedMs = [Math]::Round((Get-RapidPcBenchmarkPercentile $elapsedValues 0.95), 3)
            P50ModelDecisionMs = if ($modelDecisionValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $modelDecisionValues 0.50), 3)
            }
            P95ModelDecisionMs = if ($modelDecisionValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $modelDecisionValues 0.95), 3)
            }
            P50FirstDecisionDeltaMs = if ($firstDecisionValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $firstDecisionValues 0.50), 3)
            }
            P95FirstDecisionDeltaMs = if ($firstDecisionValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $firstDecisionValues 0.95), 3)
            }
            P50OutputFillMs = if ($outputFillValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $outputFillValues 0.50), 3)
            }
            P95OutputFillMs = if ($outputFillValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $outputFillValues 0.95), 3)
            }
            P50RequestBuildMs = if ($requestBuildValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $requestBuildValues 0.50), 3)
            }
            P50DecisionParseMs = if ($parseValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $parseValues 0.50), 3)
            }
            P50PolicyEvaluationMs = if ($policyValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $policyValues 0.50), 3)
            }
            CompletionGuardChecks = [int](($completionGuardCheckValues | Measure-Object -Sum).Sum)
            CompletionGuardMatches = [int](($completionGuardMatchValues | Measure-Object -Sum).Sum)
            P50CompletionGuardMs = if ($completionGuardValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $completionGuardValues 0.50), 3)
            }
            P50LoopPreparationMs = if ($loopPreparationValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $loopPreparationValues 0.50), 3)
            }
            P50ProviderWallMs = if ($providerWallValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $providerWallValues 0.50), 3)
            }
            P50DecisionRouteMs = if ($decisionRouteValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $decisionRouteValues 0.50), 3)
            }
            P50ProviderImageStageMs = if ($imageStageValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $imageStageValues 0.50), 3)
            }
            P50ProviderSessionSetupMs = if ($sessionSetupValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $sessionSetupValues 0.50), 3)
            }
            P50FixtureVerifyWallMs = if ($fixtureVerifyValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $fixtureVerifyValues 0.50), 3)
            }
            P50InitialCaptureMs = if ($initialCaptureValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $initialCaptureValues 0.50), 3)
            }
            P50InitialCaptureEncodeMs = if ($initialEncodeValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $initialEncodeValues 0.50), 3)
            }
            P50CaptureMs = if ($captureValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $captureValues 0.50), 3)
            }
            P95CaptureMs = if ($captureValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $captureValues 0.95), 3)
            }
            P50SettleMs = if ($settleValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $settleValues 0.50), 3)
            }
            P50ActionExecutionMs = if ($actionExecutionValues.Count -eq 0) { $null } else {
                [Math]::Round((Get-RapidPcBenchmarkPercentile $actionExecutionValues 0.50), 3)
            }
        })
    }

    return @($summaries)
}
