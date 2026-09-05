using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RapidPcUse;
using RapidPcUse.Agent;
using RapidPcUse.Agent.Providers;
using RapidPcUse.Knowledge;

if (args is ["--process-isolation-probe"])
{
    var stdin = Console.In.ReadToEnd();
    Console.WriteLine($"secret={Environment.GetEnvironmentVariable("RAPID_PC_USE_PROCESS_SECRET") ?? "<null>"};stdin={stdin.Length}");
    return 0;
}

if (args is ["--output-flood-probe"])
{
    Console.Out.Write(new string('o', 32_768));
    Console.Error.Write(new string('e', 32_768));
    return 0;
}

if (args is ["--hold-lock-probe", var lockPath])
{
    using var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Thread.Sleep(TimeSpan.FromMinutes(5));
    return 0;
}

if (args is ["--spawn-child-probe", var dotnetHost, var pidPath, var lockPathForChild])
{
    Thread.Sleep(200);
    using var child = Process.Start(new ProcessStartInfo(dotnetHost)
    {
        UseShellExecute = false,
        ArgumentList = { typeof(RunbookFeatureTests).Assembly.Location, "--hold-lock-probe", lockPathForChild },
    }) ?? throw new InvalidOperationException("Could not start containment child fixture.");
    File.WriteAllText(pidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Thread.Sleep(TimeSpan.FromMinutes(5));
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("agent defaults use Sol with medium reasoning", AgentDefaultsUseSolMedium),
    ("Codex-session provider is keyless, ephemeral, and current-frame only", CodexSessionRequestIsBounded),
    ("Codex-session provider completes a live keyless protocol turn when requested", CodexSessionLiveTurn),
    ("OpenAI requests are stateless and current-frame only", OpenAiRequestIsBounded),
    ("OpenAI requests apply the configured service tier", OpenAiRequestUsesConfiguredServiceTier),
    ("OpenAI streaming returns one strict decision", OpenAiStreamingDecisionParses),
    ("broker provider authenticates one bounded structured decision", BrokerProviderReturnsStructuredDecision),
    ("broker provider rejects a mismatched response identity", BrokerProviderRejectsMismatchedIdentity),
    ("provider failures do not expose response bodies", ProviderFailureIsRedacted),
    ("provider failure diagnostics identify a safe stage and reason", ProviderFailureDiagnosticsAreSafe),
    ("truncated provider streams fail closed", TruncatedProviderStreamFailsClosed),
    ("agent loop completes through replay provider", ReplayLoopCompletes),
    ("local completion guard eliminates the final model barrier", CompletionGuardEliminatesFinalModelBarrier),
    ("unmatched completion guard falls back to model verification", UnmatchedCompletionGuardFallsBack),
    ("MCP pc_run completes in one compact outer response", McpRunIsOneCompactResponse),
    ("MCP pc_run normalizes oversized outer-agent budgets", McpRunNormalizesOversizedBudgets),
    ("MCP pc_run accepts real Windows process names with spaces", McpRunAcceptsSpacedProcessNames),
    ("invalid MCP pc_run arguments are recoverable", McpRunRejectsInvalidArgumentsWithoutFailure),
    ("MCP pc_run uses trusted fast start context once before the first model turn", McpRunUsesTrustedFastStart),
    ("trusted fast start fails closed before a model sees the wrong foreground", FastStartActivationFailureBlocksBeforeModel),
    ("MCP pc_act returns recoverable validation feedback without stopping control", McpActValidationIsRecoverable),
    ("agent loop corrects a rejected action without releasing control", AgentLoopCorrectsRejectedAction),
    ("stale frames refresh without terminating low-level control", StaleFrameRefreshes),
    ("provider faults retry inside the high-level loop", ProviderFaultRetries),
    ("provider exhaustion reports a completed direct launch", ProviderExhaustionReportsCompletedLaunch),
    ("partial native execution replans from a fresh screenshot", PartialExecutionRecovers),
    ("public and inner scroll schemas publish model-native delta limits", ScrollSchemasUseSharedLimits),
    ("MCP knowledge update schema matches operation-specific handler inputs", McpKnowledgeUpdateSchemaMatchesHandler),
    ("MCP knowledge failure preserves a paused desktop run", McpKnowledgeFailurePreservesPausedRun),
    ("confirmation pauses and resumes without retaining a frame", ConfirmationPausesAndResumes),
    ("outer assistance pauses and continues without carrying approval", HandoffPausesAndContinues),
    ("outer assistance expires and requires a nonempty schema request", HandoffExpiryAndSchemaAreBounded),
    ("outer assistance clears one-shot confirmation authority", HandoffClearsApprovedRisk),
    ("remote content changes use their own authority boundary", RemoteContentChangeUsesDedicatedScope),
    ("PC knowledge persists bounded versioned facts atomically", RunbookFeatureTests.KnowledgeStorePersistsBoundedFacts),
    ("PC knowledge rejects oversized updates without replacing the store", RunbookFeatureTests.KnowledgeStoreRejectsOversizedUpdates),
    ("driver-local retrieval and runbook launch stay inside the fast loop", RunbookFeatureTests.LocalRouteStaysInsideFastLoop),
    ("runbook steps require retrieval in the current run", RunbookFeatureTests.UnretrievedRunbookIsRejected),
    ("runbook execution is bound to the exact retrieved step snapshot", RunbookFeatureTests.InventedRunbookStepIsRejected),
    ("local runbook launch has an explicit one-shot authority boundary", RunbookFeatureTests.RunbookLaunchRequiresAuthority),
    ("trusted local app steps enforce their declared effect authority", RunbookFeatureTests.RunbookEffectRequiresAuthority),
    ("effectful process steps accumulate exact one-shot authority", RunbookFeatureTests.EffectfulProcessAccumulatesExactAuthority),
    ("effectful runbook attempts consume budget and cannot be repeated", RunbookFeatureTests.EffectfulRunbookAttemptIsAtMostOnce),
    ("irrelevant fuzzy runbooks do not arm finish verifiers", RunbookFeatureTests.IrrelevantRunbookDoesNotGateFinish),
    ("required read-only runbook verification blocks premature finish", RunbookFeatureTests.RequiredRunbookVerificationBlocksFinish),
    ("required runbook verification also gates local completion guards", RunbookFeatureTests.RequiredRunbookVerificationGatesCompletionGuard),
    ("native mutations invalidate prior runbook verification", RunbookFeatureTests.NativeMutationRearmsRunbookVerifier),
    ("elevated runbook targets hand off before secure desktop", RunbookFeatureTests.ElevatedRunbookHandsOffBeforeDispatch),
    ("PC runbooks persist trusted structured routes without leaking paths inward", RunbookFeatureTests.RunbookStorePersistsStructuredRoutes),
    ("trusted command runbooks execute fixed arguments with bounded output", RunbookFeatureTests.TrustedProcessRunbookExecutesFixedArguments),
    ("trusted commands close stdin and remove inherited secrets", RunbookFeatureTests.TrustedProcessIsolatesInputAndEnvironment),
    ("trusted command timeout contains descendants and output floods", RunbookFeatureTests.TrustedProcessContainsLifetimeAndOutput),
    ("verified runbook performance persists and ranks equivalent routes", RunbookFeatureTests.RunbookPerformanceLearnsRoutePreference),
    ("terminal profiling includes route-learning persistence", RunbookFeatureTests.RouteLearningIsIncludedInElapsedTime),
    ("runbook result context remains bounded end to end", RunbookFeatureTests.RunbookResultContextIsBounded),
    ("malformed local memory cannot terminate an automatic PC run", RunbookFeatureTests.CorruptMemoryDoesNotTerminateRun),
    ("runbook app interfaces are loopback-only and effect-attributed", RunbookFeatureTests.RunbookLocalInterfacesAreLoopbackAndAttributed),
    ("MCP runbook schema matches the structured route handler", RunbookFeatureTests.McpRunbookSchemaMatchesHandler),
    ("web fast start never selects a competing foreground browser", RunbookFeatureTests.FastStartRejectsCompetingBrowser),
    ("foreground process scope blocks out-of-scope input", ProcessScopeBlocksInput),
    ("repeated no-progress actions trigger recovery and continue", NoProgressRecovers),
    ("physical Escape cancels an in-flight provider call", TakeoverCancelsProvider),
    ("physical Escape returns the canonical signal to the outer MCP caller", TakeoverReturnsToOuterMcp),
    ("decision state limits reject image retention", StateLimitsRejectImageData),
    ("parallel capture telemetry reports wall time separately", ParallelCaptureTelemetryUsesWallTime),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

var exitCode = failures.Count == 0 ? 0 : 1;
DriverLog.FlushAndStop(TimeSpan.FromSeconds(2));
return exitCode;

static Task AgentDefaultsUseSolMedium()
{
    var names = new[] { "RAPID_PC_AGENT_MODEL", "RAPID_PC_AGENT_REASONING" };
    var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    try
    {
        foreach (var name in names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        var options = PcAgentOptions.FromEnvironment();
        Assert(options.Model == "gpt-5.6-sol", "the default inner model is not Sol");
        Assert(options.ReasoningEffort == "medium", "the default inner reasoning effort is not medium");
    }
    finally
    {
        foreach (var pair in previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    return Task.CompletedTask;
}

static Task ProviderFailureDiagnosticsAreSafe()
{
    const string secret = "sensitive-provider-response";
    var exception = PcModelProviderStageException.Wrap(
        "turn_wait",
        new CodexAppServerException("turn_not_completed", secret));
    var failure = ProviderFailureDiagnostics.Capture(exception);
    Assert(failure.Stage == "turn_wait", "provider diagnostics lost the failure stage");
    Assert(failure.ReasonCode == "turn_not_completed", "provider diagnostics lost the safe reason code");
    Assert(failure.ExceptionType.EndsWith(nameof(CodexAppServerException), StringComparison.Ordinal),
        "provider diagnostics lost the root exception type");
    Assert(!JsonSerializer.Serialize(failure).Contains(secret, StringComparison.Ordinal),
        "provider diagnostics exposed the provider response");
    return Task.CompletedTask;
}

static Task CodexSessionRequestIsBounded()
{
    var options = Options() with { Provider = "codex" };
    using var provider = new CodexAppServerProvider(options, "codex.exe");
    using var threadDocument = JsonDocument.Parse(provider.BuildThreadStartRequest(7));
    var thread = threadDocument.RootElement;
    Assert(thread.GetProperty("method").GetString() == "thread/start", "Codex thread method is wrong");
    var threadParameters = thread.GetProperty("params");
    Assert(threadParameters.GetProperty("ephemeral").GetBoolean(), "decision threads must be ephemeral");
    Assert(threadParameters.GetProperty("serviceTier").GetString() == "fast", "fast service tier is missing");
    Assert(threadParameters.GetProperty("approvalPolicy").GetString() == "never", "inner thread approvals must be disabled");
    Assert(!thread.GetRawText().Contains("token", StringComparison.OrdinalIgnoreCase), "authentication material belongs to Codex");

    var request = new PcModelTurnRequest(
        "Open the harmless fixture.",
        Scope(),
        new AgentWorkingState("Only compact memory", [], "", [], []),
        [],
        Observation(1, [1, 2, 3]),
        2,
        10,
        null,
        "run-1");
    var images = new[] { @"C:\Temp\current-1.jpg", @"C:\Temp\current-2.jpg" };
    using var turnDocument = JsonDocument.Parse(provider.BuildTurnStartRequest(8, "thread-1", request, images));
    var turn = turnDocument.RootElement.GetProperty("params");
    Assert(turn.GetProperty("input").GetArrayLength() == 3, "turn should contain text plus only current images");
    Assert(turn.GetProperty("input")[1].GetProperty("path").GetString() == images[0], "first current image is missing");
    Assert(turn.GetProperty("input")[2].GetProperty("path").GetString() == images[1], "second current image is missing");
    Assert(turn.GetProperty("outputSchema").GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
        "structured decision schema must be strict");
    Assert(!turn.GetRawText().Contains("previous_response", StringComparison.OrdinalIgnoreCase),
        "a decision turn must not chain prior screenshot context");

    const string output = "{\"decision\":\"finish\",\"actions\":[],\"memory\":\"Visible\",\"expected_change\":\"\",\"risk_flags\":[],\"summary\":\"Done\",\"visible_evidence\":\"Fixture visible\",\"operation_summary\":\"\",\"risk\":\"none\",\"reason\":\"\"}";
    Assert(PcAgentDecisionParser.ParseStructured(output) is FinishDecision { Summary: "Done" },
        "structured Codex output did not parse");
    return Task.CompletedTask;
}

static async Task CodexSessionLiveTurn()
{
    if (Environment.GetEnvironmentVariable("RAPID_PC_AGENT_LIVE_TEST") != "1")
    {
        return;
    }

    var options = Options() with { Provider = "codex" };
    using var provider = new CodexAppServerProvider(options);
    var jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EH//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EH//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EH//2Q==");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var result = await provider.DecideAsync(
        new PcModelTurnRequest(
            "This is a provider protocol test. Do not act. Return blocked because the blank fixture has no actionable UI.",
            Scope(),
            AgentWorkingState.Empty,
            [],
            Observation(1, jpeg),
            1,
            1,
            null,
            "live-probe"),
        timeout.Token);
    Assert(result.Decision is BlockedDecision or FinishDecision, "live Codex turn returned an unexpected decision");
    Assert(result.Provider == "codex-session", "live Codex turn did not use session auth provider");
}

static Task OpenAiRequestIsBounded()
{
    var options = Options();
    using var provider = new OpenAiResponsesProvider("test-key", options, new HttpClient(new NeverSendHandler()));
    var observation = Observation(1, [1, 2, 3, 4]);
    var request = new PcModelTurnRequest(
        "Open the harmless fixture.",
        Scope(),
        AgentWorkingState.Empty,
        [],
        observation,
        1,
        10,
        null);
    var payload = provider.BuildRequest(request);
    using var document = JsonDocument.Parse(payload);
    var root = document.RootElement;
    Assert(root.GetProperty("store").ValueKind == JsonValueKind.False, "store must be false");
    Assert(root.GetProperty("stream").GetBoolean(), "stream must be enabled");
    Assert(!root.TryGetProperty("previous_response_id", out _), "request must not chain a prior response");
    Assert(!root.TryGetProperty("conversation", out _), "request must not create a conversation");
    var content = root.GetProperty("input")[0].GetProperty("content");
    Assert(content.GetArrayLength() == 2, "request should contain one text item and one current image");
    var image = content[1].GetProperty("image_url").GetString()!;
    Assert(image.EndsWith(Convert.ToBase64String([1, 2, 3, 4]), StringComparison.Ordinal), "current image bytes are missing");
    Assert(!Encoding.UTF8.GetString(payload).Contains("test-key", StringComparison.Ordinal), "API key leaked into payload");
    return Task.CompletedTask;
}

static Task OpenAiRequestUsesConfiguredServiceTier()
{
    var options = Options() with { ServiceTier = "flex" };
    using var provider = new OpenAiResponsesProvider("test-key", options, new HttpClient(new NeverSendHandler()));
    var payload = provider.BuildRequest(new PcModelTurnRequest(
        "Open the harmless fixture.",
        Scope(),
        AgentWorkingState.Empty,
        [],
        Observation(1, [1]),
        1,
        10,
        null));
    using var document = JsonDocument.Parse(payload);
    Assert(document.RootElement.GetProperty("service_tier").GetString() == "flex",
        "the direct Responses request ignored the configured service tier");
    return Task.CompletedTask;
}

static Task ParallelCaptureTelemetryUsesWallTime()
{
    var observation = new Observation(
        1,
        "fixture",
        [FrameWithCaptureMicroseconds(70_000), FrameWithCaptureMicroseconds(80_000)],
        85,
        true);
    Assert(AgentTelemetry.CaptureWallMicroseconds(observation) == 85_000,
        "parallel capture telemetry summed workers instead of reporting elapsed wall time");
    Assert(observation.Frames.Sum(frame => frame.Timings.TotalMicroseconds) == 150_000,
        "the telemetry fixture does not distinguish worker time from wall time");
    return Task.CompletedTask;
}

static async Task OpenAiStreamingDecisionParses()
{
    const string arguments = "{\"summary\":\"Done\",\"memory\":\"Visible result\",\"visible_evidence\":\"Fixture is open\"}";
    var escapedArguments = JsonSerializer.Serialize(arguments);
    var stream = $"data: {{\"type\":\"response.output_item.done\",\"item\":{{\"type\":\"function_call\",\"name\":\"computer_finish\",\"arguments\":{escapedArguments}}}}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1200,\"input_tokens_details\":{\"cached_tokens\":1024},\"output_tokens\":20,\"output_tokens_details\":{\"reasoning_tokens\":4}}}}\n\n";
    var handler = new SseHandler(stream);
    using var provider = new OpenAiResponsesProvider("test-key", Options(), new HttpClient(handler));
    var result = await provider.DecideAsync(
        new PcModelTurnRequest("Open fixture.", Scope(), AgentWorkingState.Empty, [], Observation(1, [1]), 1, 10, null),
        CancellationToken.None);
    Assert(result.Decision is FinishDecision { Summary: "Done" }, "finish decision was not parsed");
    Assert(result.Usage.InputTokens == 1200 && result.Usage.CachedInputTokens == 1024, "usage counters were not parsed");
    Assert(handler.RequestPayload is not null, "request payload was not captured");
}

static async Task BrokerProviderReturnsStructuredDecision()
{
    var pipeName = $"rapid-pc-use-test-{Guid.NewGuid():N}";
    var token = new string('a', 64);
    using var server = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync() ?? throw new InvalidOperationException("Missing broker request.");
        using var request = JsonDocument.Parse(line);
        var root = request.RootElement;
        Assert(root.GetProperty("protocolVersion").GetInt32() == 1, "broker protocol version should be explicit");
        Assert(root.GetProperty("token").GetString() == token, "broker token should authenticate the request");
        Assert(root.GetProperty("images").GetArrayLength() == 1, "broker request should contain only current images");
        Assert(root.GetProperty("decisionSchema").GetProperty("additionalProperties").GetBoolean() == false,
            "broker should carry the closed structured decision schema");
        var requestId = root.GetProperty("requestId").GetString();
        var response = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId,
            ok = true,
            functionName = "computer_decide",
            arguments = "{\"decision\":\"finish\",\"summary\":\"Done.\",\"memory\":\"\",\"visible_evidence\":\"Fixture complete.\"}",
            provider = "codex",
            model = "gpt-5.6-sol",
            timings = new
            {
                attachmentMicroseconds = 10,
                prepareMicroseconds = 20,
                firstEventMicroseconds = 30,
                firstDecisionMicroseconds = 40,
                decisionCompleteMicroseconds = 50,
            },
            usage = new
            {
                inputTokens = 100,
                cachedInputTokens = 80,
                outputTokens = 20,
                reasoningTokens = 5,
            },
        });
        await writer.WriteLineAsync(response);
    });

    using var provider = new BrokeredModelProvider(Options() with
    {
        Provider = "broker",
        Model = "gpt-5.6-sol",
    }, pipeName, token);
    var result = await provider.DecideAsync(
        new PcModelTurnRequest(
            "Complete fixture.",
            Scope(),
            AgentWorkingState.Empty,
            [],
            Observation(1, [1, 2, 3]),
            1,
            10,
            null,
            "broker-test"),
        CancellationToken.None);
    await serverTask;

    Assert(result.Decision is FinishDecision, "broker response should parse through the shared decision parser");
    Assert(result.Provider == "codex" && result.Model == "gpt-5.6-sol",
        "broker should preserve the exact DSH provider route");
    Assert(result.Usage.CachedInputTokens == 80, "broker should preserve cache usage telemetry");
    Assert(result.LocalTimings.ImageStageMicroseconds == 10, "broker should preserve attachment timing");
}

static async Task BrokerProviderRejectsMismatchedIdentity()
{
    var pipeName = $"rapid-pc-use-test-{Guid.NewGuid():N}";
    var token = new string('b', 64);
    using var server = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _ = await reader.ReadLineAsync();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId = "wrong-request",
            ok = false,
            errorCode = "fixture",
        }));
    });

    using var provider = new BrokeredModelProvider(Options() with { Provider = "broker" }, pipeName, token);
    await ExpectAsync<InvalidOperationException>(() => provider.DecideAsync(
        new PcModelTurnRequest(
            "Complete fixture.", Scope(), AgentWorkingState.Empty, [], Observation(1, [1]), 1, 10, null),
        CancellationToken.None));
    await serverTask;
}

static async Task ProviderFailureIsRedacted()
{
    const string secret = "provider-secret-error-body";
    using var provider = new OpenAiResponsesProvider(
        "test-key",
        Options(),
        new HttpClient(new ErrorHandler(HttpStatusCode.TooManyRequests, secret)));
    try
    {
        _ = await provider.DecideAsync(
            new PcModelTurnRequest("Open fixture.", Scope(), AgentWorkingState.Empty, [], Observation(1, [1]), 1, 10, null),
            CancellationToken.None);
    }
    catch (PcAgentProviderException exception)
    {
        Assert(exception.StatusCode == HttpStatusCode.TooManyRequests, "provider status was lost");
        Assert(!exception.ToString().Contains(secret, StringComparison.Ordinal), "provider response body leaked into exception");
        return;
    }

    throw new InvalidOperationException("Expected provider failure.");
}

static async Task TruncatedProviderStreamFailsClosed()
{
    const string arguments = "{\"summary\":\"Done\",\"memory\":\"Visible\",\"visible_evidence\":\"Visible\"}";
    var stream = $"data: {{\"type\":\"response.output_item.done\",\"item\":{{\"type\":\"function_call\",\"name\":\"computer_finish\",\"arguments\":{JsonSerializer.Serialize(arguments)}}}}}\n\n";
    using var provider = new OpenAiResponsesProvider("test-key", Options(), new HttpClient(new SseHandler(stream)));
    await ExpectAsync<InvalidOperationException>(async () =>
    {
        _ = await provider.DecideAsync(
            new PcModelTurnRequest("Open fixture.", Scope(), AgentWorkingState.Empty, [], Observation(1, [1]), 1, 10, null),
            CancellationToken.None);
    });
}

static Task ReplayLoopCompletes()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    var state = new AgentWorkingState("Fixture visible", [], "Click target", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Target changes", new HashSet<PcRiskFlag>()),
        new FinishDecision("Fixture completed.", state, "Target changed"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var result = loop.Run(Request(maxNoProgress: 3));
    Assert(result.Status == PcAgentStatus.Completed, "run did not complete");
    Assert(result.ActionsExecuted == 1 && desktop.ActionBatches.Count == 1, "expected action was not executed exactly once");
    Assert(desktop.ActiveWindowObserveCount == 1, "agent loop did not begin from an active-window observation");
    Assert(desktop.StopCount == 1, "desktop control was not released");
    return Task.CompletedTask;
}

static Task CompletionGuardEliminatesFinalModelBarrier()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    var state = new AgentWorkingState("Fixture visible", [], "Click target", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(
            actions.RootElement.Clone(),
            state,
            "Completion banner appears",
            new HashSet<PcRiskFlag>(),
            "FIXTURE COMPLETE",
            "Fixture completed."),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    var verifier = new FixedCompletionGuardVerifier(matched: true);
    using var loop = new PcAgentLoop(
        desktop,
        provider,
        Options(),
        new FixedWindowInspector(),
        completionGuard: verifier);

    var result = loop.Run(Request(maxNoProgress: 3));

    Assert(result.Status == PcAgentStatus.Completed, "matching completion guard did not finish the run");
    Assert(result.ModelTurns == 1, "completion guard did not eliminate the final model turn");
    Assert(result.Summary == "Fixture completed.", "completion guard summary was not returned");
    Assert(verifier.Calls == 1, "completion guard was not evaluated exactly once");
    Assert(desktop.ActionBatches.Count == 1, "guarded action batch did not execute exactly once");
    return Task.CompletedTask;
}

static Task UnmatchedCompletionGuardFallsBack()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    var state = new AgentWorkingState("Fixture visible", [], "Click target", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(
            actions.RootElement.Clone(),
            state,
            "Completion banner appears",
            new HashSet<PcRiskFlag>(),
            "FIXTURE COMPLETE",
            "Fixture completed."),
        new FinishDecision("Fixture verified by the model.", state, "Target changed"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    var verifier = new FixedCompletionGuardVerifier(matched: false);
    using var loop = new PcAgentLoop(
        desktop,
        provider,
        Options(),
        new FixedWindowInspector(),
        completionGuard: verifier);

    var result = loop.Run(Request(maxNoProgress: 3));

    Assert(result.Status == PcAgentStatus.Completed, "unmatched completion guard blocked normal completion");
    Assert(result.ModelTurns == 2, "unmatched completion guard did not fall back to model verification");
    Assert(result.Summary == "Fixture verified by the model.", "fallback model summary was not returned");
    Assert(verifier.Calls == 1, "unmatched completion guard was not evaluated exactly once");
    return Task.CompletedTask;
}

static Task McpRunIsOneCompactResponse()
{
    var state = new AgentWorkingState("Fixture visible", ["Fixture completed"], "", [], []);
    using var provider = new ReplayPcModelProvider([new FinishDecision("Fixture completed.", state, "Visible fixture")]);
    using var desktop = new FakeDesktop([Observation(1, [1, 2, 3])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Complete the harmless fixture.\"}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    var server = new McpServer(desktop, loop, reader, writer);
    server.Run();
    var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    Assert(lines.Length == 1, "pc_run should produce one outer MCP response");
    using var response = JsonDocument.Parse(lines[0]);
    var result = response.RootElement.GetProperty("result");
    Assert(result.GetProperty("structuredContent").GetProperty("status").GetString() == "completed", "MCP result status is wrong");
    Assert(result.GetProperty("content").GetArrayLength() == 1, "default result should contain compact text only");
    Assert(!writer.ToString().Contains(Convert.ToBase64String([1, 2, 3]), StringComparison.Ordinal), "screenshot leaked into outer context");
    return Task.CompletedTask;
}

static Task McpRunNormalizesOversizedBudgets()
{
    var state = new AgentWorkingState("Fixture visible", ["Fixture completed"], "", [], []);
    using var provider = new ReplayPcModelProvider([new FinishDecision("Fixture completed.", state, "Visible fixture")]);
    using var desktop = new FakeDesktop([Observation(1, [1, 2, 3])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Configure a VIMLE sofa without purchasing it.\",\"scope\":{\"allowed_processes\":[\"chrome\",\"msedge\",\"firefox\"],\"allow_external_communication\":false,\"allow_local_deletion\":false,\"allow_credentials\":false,\"allow_purchases\":false,\"allow_account_or_permission_changes\":false},\"limits\":{\"max_duration_ms\":600000,\"max_actions\":200,\"max_model_turns\":50,\"max_consecutive_no_progress_turns\":8},\"return_final_screenshot\":false}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    var server = new McpServer(desktop, loop, reader, writer);
    server.Run();

    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var result = response.RootElement.GetProperty("result");
    Assert(result.GetProperty("structuredContent").GetProperty("status").GetString() == "completed",
        "oversized optional budgets should be normalized instead of terminating pc_run");
    Assert(desktop.StopCount == 1, "normalized run should release control normally");
    return Task.CompletedTask;
}

static Task McpRunAcceptsSpacedProcessNames()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), AgentWorkingState.Empty, "Change", new HashSet<PcRiskFlag>()),
        new FinishDecision("Fixture completed.", AgentWorkingState.Empty, "Visible fixture"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new NamedWindowInspector("DeepSeek Harness Desktop"));
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Complete the harmless fixture.\",\"scope\":{\"allowed_processes\":[\"DeepSeek Harness Desktop\"]}}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    new McpServer(desktop, loop, reader, writer).Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var result = response.RootElement.GetProperty("result");
    Assert(result.GetProperty("structuredContent").GetProperty("status").GetString() == "completed",
        "a valid Windows process name with spaces did not complete");
    Assert(desktop.ActionBatches.Count == 1, "matching spaced process scope blocked native input");
    return Task.CompletedTask;
}

static Task McpRunRejectsInvalidArgumentsWithoutFailure()
{
    using var provider = new RecordingProvider([]);
    using var desktop = new FakeDesktop([]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Complete the harmless fixture.\",\"scope\":{\"allowed_processes\":[\"C:\\\\untrusted.exe\"]}}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    new McpServer(desktop, loop, reader, writer).Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var result = response.RootElement.GetProperty("result");
    Assert(!result.GetProperty("isError").GetBoolean(), "invalid outer arguments became a terminal MCP failure");
    Assert(result.GetProperty("structuredContent").GetProperty("status").GetString() == "request_rejected",
        "invalid outer arguments did not return a recoverable rejection");
    Assert(desktop.StopCount == 0 && desktop.ActionBatches.Count == 0,
        "request rejection changed desktop control state");
    Assert(provider.Requests.Count == 0, "request rejection reached the model provider");
    return Task.CompletedTask;
}

static Task McpRunUsesTrustedFastStart()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"wait\",\"ms\":0}]");
    var state = new AgentWorkingState("Fixture visible", [], "Finish", [], []);
    using var provider = new RecordingProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Fixture settles", new HashSet<PcRiskFlag>()),
        new FinishDecision("Fixture completed.", state, "Fixture visible"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3])]);
    var launcher = new FakeLaunchCoordinator();
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector(), launcher: launcher);
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Complete the harmless fixture.\",\"execution_context\":\"Trusted fixture route.\",\"launch_uri\":\"https://example.com/fixture\"}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    new McpServer(desktop, loop, reader, writer).Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    Assert(response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("status").GetString() == "completed",
        "fast-start MCP run did not complete");
    Assert(launcher.LaunchUris.SequenceEqual(["https://example.com/fixture"]), "trusted launch did not execute exactly once");
    Assert(desktop.ActiveWindowObserveCount == 2, "fast start did not capture both pre-launch and post-launch state");
    Assert(desktop.ActionBatches.Count == 1, "fast-start action did not capture its normal post-action state");
    Assert(provider.Requests.Count == 2, "fast-start fixture used the wrong number of model turns");
    Assert(provider.Requests[0].Observation.FrameId == 2, "the first model turn saw the pre-launch frame");
    Assert(provider.Requests[0].OuterContext == "Trusted fixture route.", "the first model turn missed execution context");
    Assert(string.IsNullOrEmpty(provider.Requests[1].OuterContext), "execution context leaked beyond the first model turn");
    return Task.CompletedTask;
}

static Task FastStartActivationFailureBlocksBeforeModel()
{
    var state = new AgentWorkingState("Fixture visible", [], "Finish", [], []);
    using var provider = new RecordingProvider(
    [
        new FinishDecision("This decision must never be requested.", state, "Fixture visible"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    var launcher = new FakeLaunchCoordinator(targetActivated: false, foregroundProcess: "protected-app");
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector(), launcher: launcher);
    var result = loop.Run(Request(maxNoProgress: 3) with { LaunchUri = "https://example.com/fixture" });

    Assert(result.Status == PcAgentStatus.Blocked, "failed direct activation did not block safely");
    Assert(result.Summary.Contains("protected foreground process", StringComparison.Ordinal),
        "activation blocker was not explained");
    Assert(provider.Requests.Count == 0, "a model turn received the unrelated foreground frame");
    Assert(desktop.ActiveWindowObserveCount == 1, "activation failure captured an unrelated post-launch frame");
    Assert(desktop.StopCount == 1, "activation failure did not release desktop control");
    return Task.CompletedTask;
}

static Task McpActValidationIsRecoverable()
{
    using var desktop = new FakeDesktop([]);
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_act\",\"arguments\":{\"frame_id\":7,\"actions\":[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500},{\"type\":\"scroll\",\"scroll_y\":10001}]}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    var server = new McpServer(desktop, null, reader, writer);
    server.Run();

    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var result = response.RootElement.GetProperty("result");
    var text = result.GetProperty("content")[0].GetProperty("text").GetString();
    var structured = result.GetProperty("structuredContent");
    Assert(text is not null && text.Contains("PC_ACTION_REJECTED", StringComparison.Ordinal), "recoverable marker is missing");
    Assert(!result.GetProperty("isError").GetBoolean(), "recoverable rejection must not be a terminal tool error");
    Assert(structured.GetProperty("field").GetString() == "scroll_y", "rejected field is missing");
    Assert(structured.GetProperty("action_index").GetInt32() == 2, "invalid later action was not identified");
    Assert(structured.GetProperty("supplied_value").GetInt64() == 10001, "supplied value is missing");
    Assert(structured.GetProperty("allowed_minimum").GetInt64() == SecurityLimits.MinScrollDeltaPerAction, "minimum is wrong");
    Assert(structured.GetProperty("allowed_maximum").GetInt64() == SecurityLimits.MaxScrollDeltaPerAction, "maximum is wrong");
    Assert(!structured.GetProperty("frame_consumed").GetBoolean(), "rejected batch consumed its frame");
    Assert(structured.GetProperty("control_active").GetBoolean(), "rejected batch released control");
    Assert(desktop.ActionBatches.Count == 0, "invalid batch reached native execution");
    Assert(desktop.StopCount == 0, "recoverable rejection stopped desktop control");
    return Task.CompletedTask;
}

static Task ScrollSchemasUseSharedLimits()
{
    using var desktop = new FakeDesktop([]);
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    var server = new McpServer(desktop, null, reader, writer);
    server.Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var pcAct = response.RootElement.GetProperty("result").GetProperty("tools")
        .EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == "pc_act");
    var publicScroll = pcAct.GetProperty("inputSchema")
        .GetProperty("properties")
        .GetProperty("actions")
        .GetProperty("items")
        .GetProperty("oneOf")
        .EnumerateArray()
        .Single(schema => schema.GetProperty("properties").GetProperty("type").GetProperty("const").GetString() == "scroll")
        .GetProperty("properties")
        .GetProperty("scroll_y");
    Assert(publicScroll.GetProperty("minimum").GetInt32() == SecurityLimits.MinScrollDeltaPerAction, "public scroll minimum drifted");
    Assert(publicScroll.GetProperty("maximum").GetInt32() == SecurityLimits.MaxScrollDeltaPerAction, "public scroll maximum drifted");
    Assert(publicScroll.GetProperty("description").GetString()!.Contains("delta", StringComparison.Ordinal), "public scroll units are unclear");
    var publicClickX = pcAct.GetProperty("inputSchema")
        .GetProperty("properties")
        .GetProperty("actions")
        .GetProperty("items")
        .GetProperty("oneOf")
        .EnumerateArray()
        .Single(schema => schema.GetProperty("properties").GetProperty("type").GetProperty("const").GetString() == "click")
        .GetProperty("properties")
        .GetProperty("x");
    Assert(publicClickX.GetProperty("minimum").GetInt32() == 0 && publicClickX.GetProperty("maximum").GetInt32() == 1000,
        "public click coordinates contradict native execution");

    var options = Options();
    using var provider = new OpenAiResponsesProvider("test-key", options, new HttpClient(new NeverSendHandler()));
    var payload = provider.BuildRequest(new PcModelTurnRequest(
        "Scroll the fixture.",
        Scope(),
        AgentWorkingState.Empty,
        [],
        Observation(1, [1]),
        1,
        10,
        null));
    using var providerRequest = JsonDocument.Parse(payload);
    var computerAct = providerRequest.RootElement.GetProperty("tools")
        .EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == "computer_act");
    var scrollAction = computerAct.GetProperty("parameters")
        .GetProperty("properties")
        .GetProperty("actions")
        .GetProperty("items")
        .GetProperty("anyOf")
        .EnumerateArray()
        .Single(schema => schema.GetProperty("properties").GetProperty("type").GetProperty("enum")[0].GetString() == "scroll");
    var innerScroll = scrollAction.GetProperty("properties").GetProperty("scroll_y");
    Assert(innerScroll.GetProperty("minimum").GetInt32() == SecurityLimits.MinScrollDeltaPerAction, "inner scroll minimum drifted");
    Assert(innerScroll.GetProperty("maximum").GetInt32() == SecurityLimits.MaxScrollDeltaPerAction, "inner scroll maximum drifted");
    Assert(DesktopController.ScrollTicks(591) == 6, "591 delta units should become six wheel ticks");
    return Task.CompletedTask;
}

static Task McpKnowledgeUpdateSchemaMatchesHandler()
{
    using var desktop = new FakeDesktop([]);
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    new McpServer(desktop, null, reader, writer).Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var update = response.RootElement.GetProperty("result").GetProperty("tools")
        .EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == "pc_knowledge_update");
    var inputSchema = update.GetProperty("inputSchema");
    Assert(inputSchema.GetProperty("type").GetString() == "object", "knowledge update schema is not an MCP-compatible object root");
    var variants = inputSchema.GetProperty("oneOf").EnumerateArray().ToArray();
    Assert(variants.Length == 2, "knowledge update schema did not publish two operation variants");
    var upsert = variants.Single(variant =>
        variant.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "upsert");
    var upsertRequired = upsert.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet();
    foreach (var property in new[] { "operation", "key", "kind", "subject", "fact", "source" })
    {
        Assert(upsertRequired.Contains(property), $"upsert schema omitted required handler field {property}");
    }

    var forget = variants.Single(variant =>
        variant.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "forget");
    var forgetRequired = forget.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
    Assert(forgetRequired.SequenceEqual(new[] { "operation", "key" }), "forget schema requires fields its handler does not use");
    return Task.CompletedTask;
}

static Task McpKnowledgeFailurePreservesPausedRun()
{
    var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-knowledge-corrupt-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "knowledge.json");
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "not-json");
        var state = new AgentWorkingState("Fixture visible", [], "Resolve route", [], []);
        using var provider = new ReplayPcModelProvider(
        [
            new HandoffDecision(PcHandoffReason.NeedKnowledge, "Find the verified fixture route.", state),
            new FinishDecision("Fixture completed.", state, "Fixture visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
        var paused = loop.Run(Request(maxNoProgress: 3));
        var handoff = paused.Handoff ?? throw new InvalidOperationException("handoff is missing");
        const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_knowledge_search\",\"arguments\":{\"query\":\"fixture\"}}}\n";
        using var reader = new StringReader(input);
        using var writer = new StringWriter();
        var knowledgeTools = new PcKnowledgeTools(new PcKnowledgeStore(path));
        new McpServer(desktop, loop, reader, writer, knowledgeTools).Run();
        using var response = JsonDocument.Parse(writer.ToString().Trim());
        var result = response.RootElement.GetProperty("result");
        Assert(!result.GetProperty("isError").GetBoolean(), "knowledge failure became a terminal tool error");
        Assert(result.GetProperty("structuredContent").GetProperty("desktop_state_unchanged").GetBoolean(),
            "knowledge failure did not report the preserved desktop state");
        Assert(desktop.StopCount == 1, "knowledge failure performed desktop failure cleanup");
        var completed = loop.ResumeHandoff(paused.SessionId, handoff.HandoffId, "No saved route was available; continue visually.");
        Assert(completed.Status == PcAgentStatus.Completed, "knowledge failure destroyed the paused handoff session");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    return Task.CompletedTask;
}

static Task AgentLoopCorrectsRejectedAction()
{
    using var invalid = JsonDocument.Parse("[{\"type\":\"scroll\",\"scroll_y\":10001}]");
    using var corrected = JsonDocument.Parse("[{\"type\":\"scroll\",\"scroll_y\":300}]");
    var state = new AgentWorkingState("Fixture visible", [], "Scroll modestly", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(invalid.RootElement.Clone(), state, "List moves", new HashSet<PcRiskFlag>()),
        new ActDecision(corrected.RootElement.Clone(), state, "List moves", new HashSet<PcRiskFlag>()),
        new FinishDecision("Fixture completed.", state, "Target is visible"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var result = loop.Run(Request(maxNoProgress: 3));
    Assert(result.Status == PcAgentStatus.Completed, "agent did not recover from rejected action");
    Assert(result.ModelTurns == 3, "agent did not use a correction turn");
    Assert(result.ActionsExecuted == 1 && desktop.ActionBatches.Count == 1, "invalid action reached execution or corrected action was lost");
    Assert(desktop.StopCount == 1, "recoverable agent rejection released control early");
    return Task.CompletedTask;
}

static Task StaleFrameRefreshes()
{
    using var desktop = new FakeDesktop([Observation(2, [2])]);
    desktop.FailNextActAsStale();
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_act\",\"arguments\":{\"frame_id\":1,\"actions\":[{\"type\":\"wait\",\"ms\":0}]}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    new McpServer(desktop, null, reader, writer).Run();
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var result = response.RootElement.GetProperty("result");
    Assert(!result.GetProperty("isError").GetBoolean(), "stale frame became a terminal error");
    Assert(result.GetProperty("structuredContent").GetProperty("status").GetString() == "frame_refreshed", "fresh frame status is missing");
    Assert(desktop.StopCount == 0, "stale frame released control");
    return Task.CompletedTask;
}

static Task ProviderFaultRetries()
{
    using var provider = new FlakyProvider(2);
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var result = loop.Run(Request(maxNoProgress: 3));
    Assert(result.Status == PcAgentStatus.Completed, "provider retry did not complete");
    Assert(provider.Attempts == 3, "provider retry count is wrong");
    return Task.CompletedTask;
}

static Task ProviderExhaustionReportsCompletedLaunch()
{
    using var provider = new FlakyProvider(3);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    var launcher = new FakeLaunchCoordinator();
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector(), launcher: launcher);
    var result = loop.Run(Request(maxNoProgress: 3) with { LaunchUri = "discord:" });
    Assert(result.Status == PcAgentStatus.Blocked, "exhausted provider did not block safely");
    Assert(result.Summary.Contains("opened successfully", StringComparison.Ordinal),
        "provider failure concealed the successful direct launch");
    Assert(result.ActionsExecuted == 0 && desktop.ActionBatches.Count == 0,
        "provider failure incorrectly reported native input");
    Assert(desktop.StopCount == 1, "provider exhaustion did not release desktop control");
    return Task.CompletedTask;
}

static Task PartialExecutionRecovers()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"wait\",\"ms\":0},{\"type\":\"wait\",\"ms\":0}]");
    var state = new AgentWorkingState("Fixture visible", [], "Continue", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Change", new HashSet<PcRiskFlag>()),
        new FinishDecision("Recovered.", state, "Visible fixture"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    desktop.InterruptNextAct(completedActions: 1);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var result = loop.Run(Request(maxNoProgress: 3));
    Assert(result.Status == PcAgentStatus.Completed, "partial execution did not replan");
    Assert(result.ActionsExecuted == 1, "completed prefix was not counted exactly once");
    return Task.CompletedTask;
}

static Task ConfirmationPausesAndResumes()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"type\",\"text\":\"fixture\",\"interval_ms\":2}]");
    var state = new AgentWorkingState("Credential field visible", [], "Enter fixture", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Field changes", new HashSet<PcRiskFlag> { PcRiskFlag.CredentialEntry }),
        new ActDecision(actions.RootElement.Clone(), state, "Field changes", new HashSet<PcRiskFlag> { PcRiskFlag.CredentialEntry }),
        new FinishDecision("Credential fixture completed.", state, "Field contains fixture"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var paused = loop.Run(Request(maxNoProgress: 3));
    Assert(paused.Status == PcAgentStatus.NeedsConfirmation && paused.Confirmation is not null, "run did not pause");
    var confirmation = paused.Confirmation ?? throw new InvalidOperationException("confirmation is missing");
    Assert(desktop.ActionBatches.Count == 0, "sensitive action executed before confirmation");
    Assert(desktop.StopCount == 1, "pause did not release visible control");
    Assert(loop.RetainedImageCount == 0, "paused run retained a screenshot");
    var completed = loop.Resume(paused.SessionId, confirmation.ConfirmationId, approve: true);
    Assert(completed.Status == PcAgentStatus.Completed, "approved run did not resume to completion");
    Assert(desktop.ActionBatches.Count == 1, "approved action did not execute exactly once");
    Assert(desktop.StopCount == 2, "resumed run did not release control");
    return Task.CompletedTask;
}

static Task HandoffPausesAndContinues()
{
    var state = new AgentWorkingState("Fixture visible", [], "Resolve the app route", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new HandoffDecision(PcHandoffReason.NeedKnowledge, "Find the verified fixture route.", state),
        new FinishDecision("Fixture route resolved.", state, "Fixture remains visible"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var paused = loop.Run(Request(maxNoProgress: 3));
    Assert(paused.Status == PcAgentStatus.NeedsHandoff && paused.Handoff is not null, "run did not request outer assistance");
    Assert(paused.Confirmation is null, "handoff was confused with confirmation");
    Assert(desktop.StopCount == 1 && loop.RetainedImageCount == 0, "handoff retained desktop control or images");
    var handoff = paused.Handoff ?? throw new InvalidOperationException("handoff is missing");
    Expect<InvalidOperationException>(() => loop.ResumeHandoff("wrong-session", handoff.HandoffId, "Verified route."));
    Expect<InvalidOperationException>(() => loop.ResumeHandoff(paused.SessionId, "wrong-handoff", "Verified route."));
    var completed = loop.ResumeHandoff(paused.SessionId, handoff.HandoffId, "The verified route is fixture://main.");
    Assert(completed.Status == PcAgentStatus.Completed, "outer-assisted run did not continue");
    Assert(desktop.StopCount == 2, "continued run did not release control");
    Expect<InvalidOperationException>(() => loop.ResumeHandoff(paused.SessionId, handoff.HandoffId, "Replay."));
    return Task.CompletedTask;
}

static Task HandoffExpiryAndSchemaAreBounded()
{
    var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
    var state = new AgentWorkingState("Fixture visible", [], "Resolve route", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new HandoffDecision(PcHandoffReason.NeedKnowledge, "Find the verified fixture route.", state),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector(), timeProvider: clock);
    var paused = loop.Run(Request(maxNoProgress: 3));
    var handoff = paused.Handoff ?? throw new InvalidOperationException("handoff is missing");
    clock.Advance(SecurityLimits.AgentHandoffLifetime + TimeSpan.FromSeconds(1));
    var expired = loop.ResumeHandoff(paused.SessionId, handoff.HandoffId, "Verified route.");
    Assert(expired.Status == PcAgentStatus.Blocked, "expired handoff resumed desktop control");
    Assert(desktop.StopCount == 1, "expired handoff changed the stopped desktop state");

    using var schemaProvider = new OpenAiResponsesProvider("test-key", Options(), new HttpClient(new NeverSendHandler()));
    var payload = schemaProvider.BuildRequest(new PcModelTurnRequest(
        "Resolve the fixture route.",
        Scope(),
        AgentWorkingState.Empty,
        [],
        Observation(2, [2]),
        1,
        10,
        null));
    using var request = JsonDocument.Parse(payload);
    var handoffRequest = request.RootElement.GetProperty("tools")
        .EnumerateArray()
        .Single(tool => tool.GetProperty("name").GetString() == "computer_handoff")
        .GetProperty("parameters")
        .GetProperty("properties")
        .GetProperty("handoff_request");
    Assert(handoffRequest.GetProperty("minLength").GetInt32() == 1, "handoff tool schema allows an empty request");
    return Task.CompletedTask;
}

static Task HandoffClearsApprovedRisk()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"type\",\"text\":\"fixture\",\"interval_ms\":0}]");
    var state = new AgentWorkingState("Fixture visible", [], "Continue", [], []);
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Sensitive field changes", new HashSet<PcRiskFlag> { PcRiskFlag.CredentialEntry }),
        new HandoffDecision(PcHandoffReason.NeedKnowledge, "Resolve the verified route.", state),
        new ActDecision(actions.RootElement.Clone(), state, "Sensitive field changes", new HashSet<PcRiskFlag> { PcRiskFlag.CredentialEntry }),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var confirmationPause = loop.Run(Request(maxNoProgress: 3));
    var confirmation = confirmationPause.Confirmation ?? throw new InvalidOperationException("confirmation is missing");
    var handoffPause = loop.Resume(confirmationPause.SessionId, confirmation.ConfirmationId, approve: true);
    var handoff = handoffPause.Handoff ?? throw new InvalidOperationException("handoff is missing");
    var secondConfirmation = loop.ResumeHandoff(handoffPause.SessionId, handoff.HandoffId, "Verified route.");
    Assert(secondConfirmation.Status == PcAgentStatus.NeedsConfirmation,
        "one-shot confirmation authority crossed the handoff boundary");
    Assert(desktop.ActionBatches.Count == 0, "sensitive action executed with stale approval");
    return Task.CompletedTask;
}

static Task RemoteContentChangeUsesDedicatedScope()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"key\",\"keys\":\"DELETE\"}]");
    var decision = new ActDecision(
        actions.RootElement.Clone(),
        AgentWorkingState.Empty,
        "Remote message is removed",
        new HashSet<PcRiskFlag> { PcRiskFlag.RemoteContentChange });
    var policy = new ActionPolicy(new FixedWindowInspector());
    var denied = policy.Evaluate(decision, Scope(), approvedRisk: null);
    Assert(denied.ConfirmationRisk == PcRiskFlag.RemoteContentChange, "remote mutation did not request its dedicated authority");
    var remoteOnly = policy.Evaluate(
        decision,
        Scope() with { AllowRemoteContentChanges = true },
        approvedRisk: null);
    Assert(remoteOnly.ConfirmationRisk == PcRiskFlag.LocalDeletion,
        "a model-declared remote risk suppressed the driver's DELETE inference");
    var allowed = policy.Evaluate(
        decision,
        Scope() with { AllowRemoteContentChanges = true, AllowLocalDeletion = true },
        approvedRisk: null);
    Assert(allowed.Allowed && allowed.ConfirmationRisk is null, "fully scoped remote DELETE did not pass policy");
    return Task.CompletedTask;
}

static Task ProcessScopeBlocksInput()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), AgentWorkingState.Empty, "Change", new HashSet<PcRiskFlag>()),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new NamedWindowInspector("outside"));
    var scopedRequest = Request(maxNoProgress: 3) with
    {
        Scope = Scope() with { AllowedProcesses = new HashSet<string>(["fixture"], StringComparer.OrdinalIgnoreCase) },
    };
    var result = loop.Run(scopedRequest);
    Assert(result.Status == PcAgentStatus.Blocked, "out-of-scope foreground process was not blocked");
    Assert(desktop.ActionBatches.Count == 0, "out-of-scope input was executed");
    return Task.CompletedTask;
}

static Task NoProgressRecovers()
{
    using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
    var state = AgentWorkingState.Empty;
    using var provider = new ReplayPcModelProvider(
    [
        new ActDecision(actions.RootElement.Clone(), state, "Change", new HashSet<PcRiskFlag>()),
        new ActDecision(actions.RootElement.Clone(), state, "Change", new HashSet<PcRiskFlag>()),
        new ActDecision(actions.RootElement.Clone(), state, "Change", new HashSet<PcRiskFlag>()),
        new FinishDecision("Recovered.", state, "Visible fixture"),
    ]);
    using var desktop = new FakeDesktop([Observation(1, [9]), Observation(2, [9]), Observation(3, [9]), Observation(4, [9])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var result = loop.Run(Request(maxNoProgress: 2));
    Assert(result.Status == PcAgentStatus.Completed, "no-progress loop did not continue to recovery");
    Assert(result.ActionsExecuted == 3, "unexpected no-progress action count");
    return Task.CompletedTask;
}

static async Task TakeoverCancelsProvider()
{
    var provider = new BlockingProvider();
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    var run = Task.Run(() => loop.Run(Request(maxNoProgress: 3)));
    Assert(provider.Started.Wait(TimeSpan.FromSeconds(2)), "provider did not start");
    desktop.TriggerTakeover();
    await ExpectAsync<UserTakeoverException>(async () => await run);
    Assert(desktop.ActionBatches.Count == 0, "action executed after takeover");
}

static async Task TakeoverReturnsToOuterMcp()
{
    var provider = new BlockingProvider();
    using var desktop = new FakeDesktop([Observation(1, [1])]);
    using var loop = new PcAgentLoop(desktop, provider, Options(), new FixedWindowInspector());
    const string input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"pc_run\",\"arguments\":{\"task\":\"Complete the harmless fixture.\"}}}\n";
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    var server = new McpServer(desktop, loop, reader, writer);
    var run = Task.Run(server.Run);
    Assert(provider.Started.Wait(TimeSpan.FromSeconds(2)), "provider did not start through MCP");
    desktop.TriggerTakeover();
    await run;
    using var response = JsonDocument.Parse(writer.ToString().Trim());
    var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
    Assert(text is not null && text.Contains("USER_TAKEOVER", StringComparison.Ordinal), "outer MCP caller did not receive takeover signal");
    Assert(desktop.ActionBatches.Count == 0, "MCP route executed input after takeover");
}

static Task StateLimitsRejectImageData()
{
    const string decision = "{\"summary\":\"Done\",\"memory\":\"data:image/jpeg;base64,AAAA\",\"visible_evidence\":\"Done\"}";
    Expect<InvalidOperationException>(() => PcAgentDecisionParser.Parse("computer_finish", decision));
    return Task.CompletedTask;
}

static PcAgentOptions Options() => new(
    Enabled: true,
    Provider: "openai",
    Model: "gpt-5.6-sol",
    ReasoningEffort: "medium",
    ServiceTier: "fast",
    MaxModelTurns: 48,
    MaxActions: 96,
    MaxDurationMilliseconds: 120_000,
    MaxConsecutiveNoProgressTurns: 3,
    ImageDetail: "original");

static PcRunScope Scope() => new(
    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    AllowExternalCommunication: false,
    AllowRemoteContentChanges: false,
    AllowLocalDeletion: false,
    AllowCredentials: true,
    AllowPurchases: false,
    AllowAccountOrPermissionChanges: false);

static PcRunRequest Request(int maxNoProgress) => new(
    "Complete the harmless fixture.",
    Scope(),
    new PcRunLimits(12, 32, 30_000, maxNoProgress),
    ReturnFinalScreenshot: false);

static Observation Observation(long frameId, byte[] bytes)
{
    var monitor = new MonitorDescriptor("display-1", "fixture", 0, 0, 1280, 720, true);
    var resolution = new CaptureResolution(1280, 720, 720, "16:9", false);
    var timings = new CaptureStageTimings(0, 0, 0, 0, 0, 0, 0);
    return new Observation(
        frameId,
        "fixture-topology",
        [new ScreenFrame(frameId, monitor, 1280, 720, "image/jpeg", bytes, 0, resolution, timings)],
        0,
        true);
}

static ScreenFrame FrameWithCaptureMicroseconds(long totalMicroseconds)
{
    var monitor = new MonitorDescriptor("display-1", "fixture", 0, 0, 1280, 720, true);
    var resolution = new CaptureResolution(1280, 720, 720, "16:9", false);
    var timings = new CaptureStageTimings(0, 0, 0, 0, 0, 0, totalMicroseconds);
    return new ScreenFrame(1, monitor, 1280, 720, "image/jpeg", [1], 0, resolution, timings);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Expect<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ExpectAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal sealed class FakeDesktop(IEnumerable<Observation> observations) : IPcDesktop, IDisposable
{
    private readonly Queue<Observation> _observations = new(observations);
    private CancellationTokenSource _control = new();
    private bool _takeover;
    private bool _staleNextAct;
    private int? _partialCompletedActions;

    internal List<JsonElement> ActionBatches { get; } = [];
    internal int StopCount { get; private set; }
    internal int ActiveWindowObserveCount { get; private set; }

    public CancellationToken ControlCancellationToken => _control.Token;

    public Observation Observe(bool beginControl)
    {
        if (!beginControl || _observations.Count == 0)
        {
            throw new InvalidOperationException("No fake observation is available.");
        }

        _control.Dispose();
        _control = new CancellationTokenSource();
        _takeover = false;
        return _observations.Dequeue();
    }

    public Observation ObserveActiveWindow(bool beginControl)
    {
        ActiveWindowObserveCount++;
        return Observe(beginControl);
    }

    public DesktopActResult Act(long frameId, JsonElement actions, int settleMilliseconds, bool observeAfter)
    {
        _ = frameId;
        _ = observeAfter;
        if (_staleNextAct)
        {
            _staleNextAct = false;
            throw new StaleFrameException("Fixture frame became stale.");
        }

        DesktopController.ValidateActionPlan(actions, settleMilliseconds);
        ActionBatches.Add(actions.Clone());
        var timing = actions.EnumerateArray()
            .Select((action, index) => new ActionTiming(index + 1, action.GetProperty("type").GetString()!, 0, null, null, null, null))
            .ToArray();
        var observation = _observations.Count == 0 ? null : _observations.Dequeue();
        if (_partialCompletedActions is int completedActions)
        {
            _partialCompletedActions = null;
            return new DesktopActResult(
                observation,
                timing.Take(completedActions).ToArray(),
                settleMilliseconds,
                0,
                new DesktopActionFailure(completedActions + 1, "wait", "Fixture action was interrupted.", completedActions));
        }

        return new DesktopActResult(observation, timing, settleMilliseconds, 0);
    }

    public void ThrowIfControlLost()
    {
        if (_takeover)
        {
            throw new UserTakeoverException();
        }

        if (_control.IsCancellationRequested)
        {
            throw new ControlSessionEndedException();
        }
    }

    public void Stop()
    {
        StopCount++;
        _control.Cancel();
    }

    internal void TriggerTakeover()
    {
        _takeover = true;
        _control.Cancel();
    }

    internal void FailNextActAsStale() => _staleNextAct = true;

    internal void InterruptNextAct(int completedActions) => _partialCompletedActions = completedActions;

    public void Dispose() => _control.Dispose();
}

internal sealed class FlakyProvider(int failures) : IPcModelProvider
{
    internal int Attempts { get; private set; }
    public string Name => "flaky";
    public string Model => "fixture";

    public Task<PcModelTurnResult> DecideAsync(PcModelTurnRequest request, CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        if (Attempts <= failures)
        {
            throw new InvalidOperationException("Transient fixture response failure.");
        }

        var decision = new FinishDecision("Recovered.", AgentWorkingState.Empty, "Visible fixture");
        return Task.FromResult(new PcModelTurnResult(
            decision,
            Name,
            Model,
            0,
            1,
            1,
            0,
            new ProviderLocalStageTimings(0, 0, 0, 0),
            new ProviderTurnTimings(0, 0, 0, 0, 0),
            new ProviderUsage(0, 0, 0, 0)));
    }

    public void Dispose()
    {
    }
}

internal sealed class RecordingProvider(IEnumerable<PcAgentDecision> decisions) : IPcModelProvider
{
    private readonly Queue<PcAgentDecision> _decisions = new(decisions);

    internal List<PcModelTurnRequest> Requests { get; } = [];
    public string Name => "recording";
    public string Model => "fixture";

    public Task<PcModelTurnResult> DecideAsync(PcModelTurnRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var decision = _decisions.Dequeue();
        return Task.FromResult(new PcModelTurnResult(
            decision,
            Name,
            Model,
            0,
            request.Observation.Frames.Count,
            request.Observation.Frames.Sum(frame => frame.Bytes.Length),
            0,
            new ProviderLocalStageTimings(0, 0, 0, 0),
            new ProviderTurnTimings(0, 0, 0, 0, 0),
            new ProviderUsage(0, 0, 0, 0)));
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeLaunchCoordinator(bool targetActivated = true, string foregroundProcess = "test") : IPcLaunchCoordinator
{
    internal List<string> LaunchUris { get; } = [];

    public PcLaunchTiming Launch(string launchUri, Action checkOperation)
    {
        checkOperation();
        LaunchUris.Add(launchUri);
        return new PcLaunchTiming(100, 200, 300, "https", true, targetActivated, foregroundProcess);
    }
}

internal sealed class FixedWindowInspector : IForegroundWindowInspector
{
    public string GetProcessName() => "fixture";
}

internal sealed class FixedCompletionGuardVerifier(bool matched) : ICompletionGuardVerifier
{
    internal int Calls { get; private set; }

    public CompletionGuardResult Verify(string expectedText)
    {
        AssertExpectedText(expectedText);
        Calls++;
        return new CompletionGuardResult(matched, 1, 10, matched ? "matched" : "not_found");
    }

    private static void AssertExpectedText(string expectedText)
    {
        if (expectedText != "FIXTURE COMPLETE")
        {
            throw new InvalidOperationException("completion guard text was changed before verification");
        }
    }
}

internal sealed class NamedWindowInspector(string processName) : IForegroundWindowInspector
{
    public string GetProcessName() => processName;
}

internal sealed class BlockingProvider : IPcModelProvider
{
    internal ManualResetEventSlim Started { get; } = new(false);
    public string Name => "blocking";
    public string Model => "blocking";

    public async Task<PcModelTurnResult> DecideAsync(PcModelTurnRequest request, CancellationToken cancellationToken)
    {
        _ = request;
        Started.Set();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }

    public void Dispose() => Started.Dispose();
}

internal sealed class NeverSendHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        throw new InvalidOperationException("The request builder test must not send HTTP.");
    }
}

internal sealed class SseHandler(string responseBody) : HttpMessageHandler
{
    internal string? RequestPayload { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
        };
    }
}

internal sealed class ErrorHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan duration) => _utcNow += duration;
}
