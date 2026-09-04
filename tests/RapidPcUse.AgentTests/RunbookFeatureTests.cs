using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using RapidPcUse;
using RapidPcUse.Agent;
using RapidPcUse.Knowledge;

internal static class RunbookFeatureTests
{
    internal static Task KnowledgeStorePersistsBoundedFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-knowledge-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "knowledge.json");
        try
        {
            var store = new PcKnowledgeStore(path);
            var first = store.Upsert(
                "app.discord",
                "app",
                "Discord",
                "The preferred server is reached from the first pinned server icon.",
                "Launch Discord directly, then verify the server label before acting.",
                "verified_observation",
                90);
            Assert(first.Revision == 1, "new knowledge did not start at revision one");
            var loaded = new PcKnowledgeStore(path).Search("discord server", 4);
            Assert(loaded.Count == 1 && loaded[0].Key == "app.discord", "persisted knowledge was not searchable");
            var revised = store.Upsert(
                "app.discord",
                "app",
                "Discord",
                "The preferred server is reached from the first pinned server icon.",
                "Launch Discord directly and confirm its label.",
                "verified_observation",
                95);
            Assert(revised.Revision == 2, "knowledge revision did not advance");
            Assert(store.Forget("app.discord") && store.Search("discord", 4).Count == 0, "forgotten knowledge remained searchable");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }

        return Task.CompletedTask;
    }

    internal static Task KnowledgeStoreRejectsOversizedUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-knowledge-size-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "knowledge.json");
        try
        {
            var store = new PcKnowledgeStore(path);
            var subject = string.Concat(Enumerable.Repeat("😀", PcKnowledgeStore.MaxSubjectCharacters / 2));
            var fact = string.Concat(Enumerable.Repeat("😀", PcKnowledgeStore.MaxFactCharacters / 2));
            var navigation = string.Concat(Enumerable.Repeat("😀", PcKnowledgeStore.MaxNavigationHintCharacters / 2));
            var accepted = 0;
            var rejected = false;
            for (var index = 0; index < PcKnowledgeStore.MaxEntries; index++)
            {
                try
                {
                    store.Upsert($"large.{index}", "workflow", subject, fact, navigation, "manual", 50);
                    accepted++;
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("size limit", StringComparison.Ordinal))
                {
                    rejected = true;
                    break;
                }
            }

            Assert(rejected, "high-Unicode entries did not reach the serialized byte limit");
            Assert(new FileInfo(path).Length <= 512 * 1024, "an oversized knowledge file replaced the prior store");
            var loaded = new PcKnowledgeStore(path).Search(string.Empty, PcKnowledgeStore.MaxSearchResults);
            Assert(loaded.Count > 0 && accepted > 0, "the prior valid knowledge store became unreadable");
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

    internal static Task LocalRouteStaysInsideFastLoop()
    {
        var state = new AgentWorkingState("Need known route", [], "Launch fixture", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "launch", "Fixture window opens", state),
            new FinishDecision("Fixture route completed.", state, "Fixture window visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        var routes = new FakeLocalRouteRuntime("workflow.fixture", "launch");
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);
        var request = Request(maxNoProgress: 3) with
        {
            Scope = Scope() with { AllowLocalProcessLaunches = true },
        };

        var result = loop.Run(request);
        Assert(result.Status == PcAgentStatus.Completed, "driver-local route did not complete");
        Assert(result.ModelTurns == 2 && result.ActionsExecuted == 1, "route used the wrong turn/action count");
        Assert(routes.SearchQueries.Count == 1 &&
               routes.SearchQueries[0].Contains("harmless fixture", StringComparison.Ordinal),
            "the original task was not prefetched exactly once");
        Assert(routes.Executions.SequenceEqual(["workflow.fixture/launch"]), "trusted launch step did not execute exactly once");
        Assert(routes.PerformanceSamples.Count == 1 &&
               routes.PerformanceSamples[0].Disposition == PcRunbookExecutionDisposition.Success,
            "the completed driver run did not emit privacy-safe route learning data");
        Assert(provider.Requests[0].RetrievedContext.Contains("workflow.fixture", StringComparison.Ordinal),
            "prefetched context was not supplied on the first inner turn");
        Assert(provider.Requests[1].RetrievedContext.Contains("workflow.fixture", StringComparison.Ordinal),
            "the selected runbook did not remain available for its later ordered steps");
        Assert(desktop.StopCount == 1, "inner retrieval returned to the outer loop or leaked control");
        return Task.CompletedTask;
    }

    internal static Task UnretrievedRunbookIsRejected()
    {
        var state = new AgentWorkingState("Fixture visible", [], "Do not launch unknown route", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.unseen", "launch", "Unexpected target", state),
            new FinishDecision("Rejected safely.", state, "Fixture remains visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime("workflow.unseen", "launch", returnMatch: false);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed, "rejected route could not recover");
        Assert(result.ActionsExecuted == 0 && routes.Executions.Count == 0,
            "an unretrieved runbook key reached local process execution");
        Assert(provider.Requests[1].RetrievedContext.Contains("not returned", StringComparison.Ordinal),
            "the inner model did not receive bounded correction context");
        return Task.CompletedTask;
    }

    internal static Task InventedRunbookStepIsRejected()
    {
        var state = new AgentWorkingState("Known route retrieved", [], "Reject invented step", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "invented", "Unexpected target", state),
            new FinishDecision("Rejected safely.", state, "Fixture remains visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime("workflow.fixture", "launch");
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed, "the exact-step rejection could not recover");
        Assert(result.ActionsExecuted == 0 && routes.Executions.Count == 0,
            "an invented step under a retrieved runbook key reached dispatch");
        Assert(provider.Requests[1].RetrievedContext.Contains("not returned", StringComparison.Ordinal),
            "the inner model did not receive exact-step correction context");
        return Task.CompletedTask;
    }

    internal static Task RunbookLaunchRequiresAuthority()
    {
        var state = new AgentWorkingState("Need fixture", [], "Launch trusted fixture", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "launch", "Fixture opens", state),
            new RunbookStepDecision("workflow.fixture", "launch", "Fixture opens", state),
            new FinishDecision("Fixture opened.", state, "Fixture visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3])]);
        var routes = new FakeLocalRouteRuntime("workflow.fixture", "launch");
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var paused = loop.Run(Request(maxNoProgress: 3));
        Assert(paused.Status == PcAgentStatus.NeedsConfirmation && paused.Confirmation?.Risk == PcRiskFlag.LocalProcessLaunch,
            "unscoped local launch did not pause at its dedicated authority boundary");
        Assert(routes.Executions.Count == 0, "runbook launched before approval");
        var completed = loop.Resume(paused.SessionId, paused.Confirmation!.ConfirmationId, approve: true);
        Assert(completed.Status == PcAgentStatus.Completed, "approved runbook launch did not resume");
        Assert(routes.Executions.Count == 1 && completed.ActionsExecuted == 1,
            "one-shot launch authority executed the wrong number of steps");
        return Task.CompletedTask;
    }

    internal static Task RunbookEffectRequiresAuthority()
    {
        var state = new AgentWorkingState("Fixture visible", [], "Reset local fixture", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "reset", "Fixture resets", state),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "reset",
            stepKind: "local_http",
            effect: "local_deletion");
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.NeedsConfirmation && result.Confirmation?.Risk == PcRiskFlag.LocalDeletion,
            "the trusted local POST bypassed its declared deletion authority");
        Assert(routes.Executions.Count == 0, "the trusted local POST executed before effect approval");
        return Task.CompletedTask;
    }

    internal static Task EffectfulProcessAccumulatesExactAuthority()
    {
        var state = new AgentWorkingState("Need exact maintenance", [], "Run trusted maintenance once", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "maintain", "Maintenance completes", state),
            new RunbookStepDecision("workflow.fixture", "maintain", "Maintenance completes", state),
            new RunbookStepDecision("workflow.fixture", "maintain", "Maintenance completes", state),
            new FinishDecision("Maintenance completed.", state, "Verified fixture state"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3]), Observation(4, [4])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "maintain",
            stepKind: "process",
            effect: "local_deletion");
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var processPause = loop.Run(Request(maxNoProgress: 3));
        Assert(processPause.Status == PcAgentStatus.NeedsConfirmation &&
               processPause.Confirmation?.Risk == PcRiskFlag.LocalProcessLaunch,
            "an unscoped effectful process did not request launch authority first");
        var effectPause = loop.Resume(
            processPause.SessionId,
            processPause.Confirmation!.ConfirmationId,
            approve: true);
        Assert(effectPause.Status == PcAgentStatus.NeedsConfirmation &&
               effectPause.Confirmation?.Risk == PcRiskFlag.LocalDeletion,
            "the exact process did not retain its first approval while requesting effect authority");
        var completed = loop.Resume(
            effectPause.SessionId,
            effectPause.Confirmation!.ConfirmationId,
            approve: true);
        Assert(completed.Status == PcAgentStatus.Completed && completed.ActionsExecuted == 1,
            "the two exact one-shot approvals did not dispatch one process step");
        Assert(routes.Executions.SequenceEqual(["workflow.fixture/maintain"]),
            "multi-scope approval repeated or changed the trusted process target");
        return Task.CompletedTask;
    }

    internal static Task EffectfulRunbookAttemptIsAtMostOnce()
    {
        var state = new AgentWorkingState("Fixture visible", [], "Reset fixture once", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "reset", "Fixture resets", state),
            new RunbookStepDecision("workflow.fixture", "reset", "Fixture resets", state),
            new FinishDecision("Ambiguous effect was not repeated.", state, "Fixture remains visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "reset",
            stepKind: "local_http",
            effect: "local_deletion",
            throwAfterDispatch: true);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3) with
        {
            Scope = Scope() with { AllowLocalDeletion = true },
        });
        Assert(result.Status == PcAgentStatus.Completed, "an ambiguous effectful attempt could not recover");
        Assert(result.ActionsExecuted == 1 && routes.Executions.SequenceEqual(["workflow.fixture/reset"]),
            "the effectful attempt did not consume exactly one action or was dispatched twice");
        Assert(routes.PerformanceSamples.Count == 1 &&
               routes.PerformanceSamples[0].Disposition == PcRunbookExecutionDisposition.EffectUncertain,
            "the ambiguous effect was not learned as uncertain");
        Assert(provider.Requests[1].RetrievedContext.Contains("never repeat", StringComparison.OrdinalIgnoreCase),
            "the ambiguous POST result did not warn the inner controller against repetition");
        return Task.CompletedTask;
    }

    internal static Task IrrelevantRunbookDoesNotGateFinish()
    {
        using var provider = new RecordingProvider(
        [
            new FinishDecision("Visible task is complete.", AgentWorkingState.Empty, "Visible fixture"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.unrelated",
            "status",
            stepKind: "local_http",
            requiredBeforeFinish: true,
            activateMatch: false);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed && result.ModelTurns == 1,
            "an unrelated fuzzy runbook blocked normal completion");
        Assert(result.ActionsExecuted == 0 && routes.Executions.Count == 0,
            "an unrelated verifier was executed or consumed action budget");
        return Task.CompletedTask;
    }

    internal static Task ElevatedRunbookHandsOffBeforeDispatch()
    {
        var state = new AgentWorkingState("Fixture visible", [], "Open elevated fixture", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "elevated", "Elevated fixture opens", state),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "elevated",
            requiresElevation: true);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3) with
        {
            Scope = Scope() with { AllowLocalProcessLaunches = true },
        });
        Assert(result.Status == PcAgentStatus.NeedsHandoff && result.Handoff?.Reason == PcHandoffReason.UnsupportedCapability,
            "elevated runbook target did not hand off before secure desktop");
        Assert(routes.Executions.Count == 0, "elevated process dispatch began before the handoff");
        return Task.CompletedTask;
    }

    internal static Task RequiredRunbookVerificationBlocksFinish()
    {
        var state = new AgentWorkingState("Fixture looks stale", [], "Verify current fixture state", [], []);
        using var provider = new RecordingProvider(
        [
            new FinishDecision("The stale pixels look complete.", state, "Stale visible fixture"),
            new FinishDecision("The exact local status is verified.", state, "Trusted local status data"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "status",
            stepKind: "local_http",
            requiredBeforeFinish: true);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed && result.ModelTurns == 2,
            "the run did not complete after its required exact verifier");
        Assert(result.ActionsExecuted == 1 && routes.Executions.SequenceEqual(["workflow.fixture/status"]),
            "the required read-only verifier did not execute exactly once");
        Assert(provider.Requests[1].RetrievedContext.Contains("Trusted local step result data", StringComparison.Ordinal),
            "the premature finish was not corrected with exact verifier result data");
        return Task.CompletedTask;
    }

    internal static Task RequiredRunbookVerificationGatesCompletionGuard()
    {
        using var actions = JsonDocument.Parse("[{\"type\":\"wait\",\"ms\":0}]");
        var state = new AgentWorkingState("Fixture looks complete", [], "Verify exact fixture state", [], []);
        using var provider = new RecordingProvider(
        [
            new ActDecision(
                actions.RootElement.Clone(),
                state,
                "Completion banner appears",
                new HashSet<PcRiskFlag>(),
                "FIXTURE COMPLETE",
                "Fixture completed."),
            new FinishDecision("The exact local status is verified.", state, "Trusted local status data"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2]), Observation(3, [3])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "status",
            stepKind: "local_http",
            requiredBeforeFinish: true);
        var verifier = new FixedCompletionGuardVerifier(matched: true);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            completionGuard: verifier,
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed && result.ModelTurns == 2,
            "the completion guard bypassed or blocked the required exact verifier");
        Assert(result.ActionsExecuted == 2 && routes.Executions.SequenceEqual(["workflow.fixture/status"]),
            "the completion guard path did not execute exactly one visual action and one verifier");
        Assert(provider.Requests[1].RetrievedContext.Contains("Trusted local step result data", StringComparison.Ordinal),
            "the guard match was not corrected with exact verifier result data");
        return Task.CompletedTask;
    }

    internal static Task NativeMutationRearmsRunbookVerifier()
    {
        using var actions = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-1\",\"x\":500,\"y\":500,\"button\":\"left\",\"count\":1}]");
        var state = new AgentWorkingState("Fixture route active", [], "Verify after mutation", [], []);
        using var provider = new RecordingProvider(
        [
            new FinishDecision("Initial exact state.", state, "Fixture status"),
            new ActDecision(actions.RootElement.Clone(), state, "Fixture changes", new HashSet<PcRiskFlag>()),
            new FinishDecision("State after native mutation.", state, "Fixture status"),
            new FinishDecision("Fresh exact state.", state, "Fresh fixture status"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "status",
            stepKind: "local_http",
            requiredBeforeFinish: true);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3));
        Assert(result.Status == PcAgentStatus.Completed && result.ModelTurns == 4,
            "the route did not require fresh verification after native mutation");
        Assert(result.ActionsExecuted == 3 && routes.Executions.Count == 2,
            "the native action and two verifier epochs consumed the wrong action count");
        return Task.CompletedTask;
    }

    internal static Task RunbookStorePersistsStructuredRoutes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-runbooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "runbooks-v1.json");
            var executable = Path.Combine(directory, "Fixture.exe");
            var store = new PcRunbookStore(path);
            var created = store.Upsert(
                "workflow.fixture",
                "Fixture workflow",
                "Open a known fixture and verify its visible state.",
                ["fixture", "known project"],
                [
                    new PcRunbookStep("launch", "launch", "Open the trusted fixture entry point.", executable, directory, "fixture"),
                    new PcRunbookStep("verify", "guidance", "Verify the fixture title before finishing.", "", "", ""),
                ],
                "user",
                100);
            Assert(created.Revision == 1, "new runbook did not start at revision one");
            var loaded = new PcRunbookStore(path).Search("known fixture", 4);
            Assert(loaded.Count == 1 && loaded[0].Steps.Count == 2, "persisted runbook was not searchable");

            var legacyDocument = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var legacyRunbook = legacyDocument["runbooks"]!.AsArray()[0]!.AsObject();
            _ = legacyRunbook.Remove("stepPerformance");
            foreach (var legacyStep in legacyRunbook["steps"]!.AsArray())
            {
                _ = legacyStep!.AsObject().Remove("arguments");
                _ = legacyStep.AsObject().Remove("timeoutMilliseconds");
            }

            File.WriteAllText(path, legacyDocument.ToJsonString());
            var backwardCompatible = new PcRunbookStore(path).Search("known fixture", 4)[0];
            Assert(backwardCompatible.Steps.All(step => step.Arguments is { Count: 0 } && step.TimeoutMilliseconds == 10_000),
                "a pre-command runbook document did not receive safe command-field defaults");

            var knowledgePath = Path.Combine(directory, "knowledge-v1.json");
            var runtime = new PcLocalRouteRuntime(new PcKnowledgeStore(knowledgePath), new PcRunbookStore(path));
            var retrieval = runtime.Search("fixture");
            Assert(retrieval.RunbookKeys.Contains("workflow.fixture"), "runtime did not expose the trusted opaque runbook key");
            Assert(!retrieval.Context.Contains(executable, StringComparison.OrdinalIgnoreCase),
                "the inner retrieval context leaked an executable path");
            Assert(retrieval.Context.Contains("executable step launch", StringComparison.Ordinal),
                "the inner retrieval context omitted the selectable step ID");
            Assert(!retrieval.ActivatedRunbookKeys.Contains("workflow.fixture"),
                "a one-word fuzzy retrieval incorrectly activated finish gating");
            var strongRetrieval = runtime.Search("Please open the known project fixture.");
            Assert(strongRetrieval.ActivatedRunbookKeys.Contains("workflow.fixture"),
                "an exact multiword route phrase did not activate its verifier boundary");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task RunbookResultContextIsBounded()
    {
        var state = new AgentWorkingState("Need fixture", [], "Use bounded local result", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "launch", "Fixture opens", state),
            new FinishDecision("Fixture opened.", state, "Fixture visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "launch",
            resultContext: new string('x', 16_384));
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var result = loop.Run(Request(maxNoProgress: 3) with
        {
            Scope = Scope() with { AllowLocalProcessLaunches = true },
        });
        Assert(result.Status == PcAgentStatus.Completed, "bounded-result fixture did not complete");
        Assert(provider.Requests[1].RetrievedContext.Length <= SecurityLimits.MaxAgentRetrievedContextCharacters,
            "runbook route plus local result exceeded the inner context cap");
        Assert(provider.Requests[1].RetrievedContext.Contains("data only", StringComparison.Ordinal),
            "local result data lost its untrusted-data label");
        return Task.CompletedTask;
    }

    internal static Task TrustedProcessRunbookExecutesFixedArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-process-runbook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
            var store = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"));
            store.Upsert(
                "workflow.command-fixture",
                "Trusted command fixture",
                "Run one exact direct process without a shell and return bounded output.",
                ["trusted command fixture"],
                [
                    new PcRunbookStep(
                        "locate-command",
                        "process",
                        "Locate the Windows command interpreter through a fixed read-only query.",
                        command,
                        Path.GetDirectoryName(command)!,
                        "",
                        Arguments: ["/R", Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"],
                        TimeoutMilliseconds: 3_000),
                ],
                "manual",
                100);
            var runtime = new PcLocalRouteRuntime(
                new PcKnowledgeStore(Path.Combine(directory, "knowledge-v1.json")),
                store);
            var retrieval = runtime.Search("Run the trusted command fixture.");
            var reference = new PcRunbookStepReference("workflow.command-fixture", "locate-command");
            Assert(retrieval.ExecutableSteps.TryGetValue(reference, out var step),
                "the exact trusted command snapshot was not projected");
            Assert(retrieval.Context.Contains("trusted command step locate-command", StringComparison.Ordinal),
                "the command step was not described to the inner controller");
            Assert(!retrieval.Context.Contains(command, StringComparison.OrdinalIgnoreCase) &&
                   !retrieval.Context.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                "the command target or fixed argument leaked into inner context");

            var result = runtime.Execute(reference, step!, () => { });
            Assert(result.TargetObserved && result.StepKind == "process",
                "the exact trusted command did not complete successfully");
            Assert(result.ResultContext.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                "bounded command output was not returned as result data");
            Assert(result.ResultContext.Length <= 8_256, "trusted command output exceeded its fixed runtime bound");

            Expect<ArgumentException>(() => store.Upsert(
                "workflow.shell-fixture",
                "Rejected shell fixture",
                "Do not treat a shell script as a direct process executable.",
                ["shell fixture"],
                [new PcRunbookStep("shell", "process", "Reject shell dispatch.", "C:\\fixture.cmd", "C:\\", "")],
                "manual",
                100));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task TrustedProcessIsolatesInputAndEnvironment()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-process-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousSecret = Environment.GetEnvironmentVariable("RAPID_PC_USE_PROCESS_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("RAPID_PC_USE_PROCESS_SECRET", "must-not-cross-boundary");
            var executable = TestDotnetHost();
            var testAssembly = typeof(RunbookFeatureTests).Assembly.Location;
            var store = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"));
            var runbook = store.Upsert(
                "workflow.process-isolation",
                "Process isolation fixture",
                "Verify fixed commands cannot read driver input or inherited secrets.",
                ["process isolation fixture"],
                [new PcRunbookStep(
                    "probe",
                    "process",
                    "Read the isolated fixture input and selected environment value.",
                    executable,
                    Path.GetDirectoryName(executable)!,
                    "",
                    Arguments: [testAssembly, "--process-isolation-probe"],
                    TimeoutMilliseconds: 5_000)],
                "benchmark",
                100);
            var runtime = new PcLocalRouteRuntime(
                new PcKnowledgeStore(Path.Combine(directory, "knowledge-v1.json")),
                store);
            var reference = new PcRunbookStepReference(runbook.Key, "probe");
            var result = runtime.Execute(reference, runbook.Steps[0], () => { });
            Assert(result.TargetObserved &&
                   result.ResultContext.Contains("secret=<null>;stdin=0", StringComparison.Ordinal),
                "a trusted command inherited a secret or the driver's protocol input");
            Assert(!result.ResultContext.Contains("must-not-cross-boundary", StringComparison.Ordinal),
                "a parent environment secret reached trusted-command output");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RAPID_PC_USE_PROCESS_SECRET", previousSecret);
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task TrustedProcessContainsLifetimeAndOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-process-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = TestDotnetHost();
            var testAssembly = typeof(RunbookFeatureTests).Assembly.Location;
            var store = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"));
            var flood = store.Upsert(
                "workflow.output-flood",
                "Output flood fixture",
                "Bound a command that writes more output than the prompt may receive.",
                ["output flood fixture"],
                [new PcRunbookStep(
                    "flood",
                    "process",
                    "Write bounded fixture output.",
                    executable,
                    Path.GetDirectoryName(executable)!,
                    "",
                    Arguments: [testAssembly, "--output-flood-probe"],
                    TimeoutMilliseconds: 5_000)],
                "benchmark",
                100);
            var runtime = new PcLocalRouteRuntime(
                new PcKnowledgeStore(Path.Combine(directory, "knowledge-v1.json")),
                store);
            var floodResult = runtime.Execute(
                new PcRunbookStepReference(flood.Key, "flood"),
                flood.Steps[0],
                () => { });
            Assert(floodResult.TargetObserved &&
                   floodResult.ResultContext.Length <= 8_256 &&
                   floodResult.ResultContext.Contains("bounded output truncated", StringComparison.Ordinal),
                "a trusted command output flood was not drained and bounded");

            var pidPath = Path.Combine(directory, "child.pid");
            var lockPath = Path.Combine(directory, "child.lock");
            var timeout = store.Upsert(
                "workflow.timeout-tree",
                "Timeout tree fixture",
                "A timed-out command and its descendant must not survive control.",
                ["timeout tree fixture"],
                [new PcRunbookStep(
                    "timeout",
                    "process",
                    "Start a contained child and exceed the fixed deadline.",
                    executable,
                    Path.GetDirectoryName(executable)!,
                    "",
                    Arguments: [testAssembly, "--spawn-child-probe", executable, pidPath, lockPath],
                    TimeoutMilliseconds: 1_000)],
                "benchmark",
                100);
            Expect<TimeoutException>(() => runtime.Execute(
                new PcRunbookStepReference(timeout.Key, "timeout"),
                timeout.Steps[0],
                () => { }));
            Assert(File.Exists(pidPath), "the containment fixture did not create its descendant");
            var childId = int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            var childExited = false;
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(childId);
                childExited = child.HasExited || child.WaitForExit(1_000);
            }
            catch (ArgumentException)
            {
                childExited = true;
            }

            Assert(childExited, "a descendant survived the trusted-command timeout boundary");
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task RunbookPerformanceLearnsRoutePreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-route-learning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"));
            PcRunbook Add(string key) => store.Upsert(
                key,
                "Equivalent route fixture",
                "Two equally relevant routes differ only in measured reliability.",
                ["equivalent route"],
                [new PcRunbookStep("status", "local_http", "Read exact fixture status.", "", "", "",
                    HttpMethod: "GET", HttpUrl: "http://127.0.0.1:1/status")],
                "benchmark",
                80);

            var routeA = Add("workflow.route-a");
            var routeB = Add("workflow.route-b");
            store.RecordPerformance(
            [
                new PcRunbookExecutionSample(
                    new PcRunbookStepReference("workflow.route-a", "status"),
                    PcRunbookStore.ExecutionFingerprint(routeA.Steps[0]),
                    PcRunbookExecutionDisposition.Success,
                    2_000),
                new PcRunbookExecutionSample(
                    new PcRunbookStepReference("workflow.route-b", "status"),
                    PcRunbookStore.ExecutionFingerprint(routeB.Steps[0]),
                    PcRunbookExecutionDisposition.Failure,
                    3_000),
            ]);

            var learned = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"))
                .Search("equivalent route", 2);
            Assert(learned.Count == 2 && learned[0].Key == "workflow.route-a",
                "equally relevant routes were not ranked by persisted measured reliability");
            var metric = learned[0].StepPerformance!["status"];
            Assert(metric.Attempts == 1 && metric.Successes == 1 && metric.TotalSuccessfulMicroseconds == 2_000,
                "privacy-safe step performance did not survive a store reload");

            var revised = Add("workflow.route-a");
            Assert(revised.Revision == 2 && revised.StepPerformance!["status"].Successes == 1,
                "a semantic runbook revision discarded compatible learned performance");

            var oldFingerprint = PcRunbookStore.ExecutionFingerprint(revised.Steps[0]);
            var replaced = store.Upsert(
                revised.Key,
                revised.Title,
                revised.Summary,
                revised.SearchTerms,
                [revised.Steps[0] with { HttpUrl = "http://127.0.0.1:1/replacement" }],
                revised.Source,
                revised.Confidence);
            Assert(replaced.StepPerformance is null || !replaced.StepPerformance.ContainsKey("status"),
                "changed command semantics retained performance learned for the old target");
            store.RecordPerformance(
            [
                new PcRunbookExecutionSample(
                    new PcRunbookStepReference(replaced.Key, "status"),
                    oldFingerprint,
                    PcRunbookExecutionDisposition.Success,
                    1_000),
            ]);
            var afterStaleSample = store.Search("equivalent route", 2)
                .Single(runbook => runbook.Key == replaced.Key);
            Assert(afterStaleSample.StepPerformance is null || !afterStaleSample.StepPerformance.ContainsKey("status"),
                "an in-flight stale execution sample was attributed to a revised step");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task RouteLearningIsIncludedInElapsedTime()
    {
        var state = new AgentWorkingState("Need fixture", [], "Launch fixture", [], []);
        using var provider = new RecordingProvider(
        [
            new RunbookStepDecision("workflow.fixture", "launch", "Fixture opens", state),
            new FinishDecision("Fixture opened.", state, "Fixture visible"),
        ]);
        using var desktop = new FakeDesktop([Observation(1, [1]), Observation(2, [2])]);
        var routes = new FakeLocalRouteRuntime(
            "workflow.fixture",
            "launch",
            recordPerformanceDelayMilliseconds: 125);
        using var loop = new PcAgentLoop(
            desktop,
            provider,
            Options(),
            new FixedWindowInspector(),
            localRoutes: routes);

        var wallStarted = System.Diagnostics.Stopwatch.StartNew();
        var result = loop.Run(Request(maxNoProgress: 3) with
        {
            Scope = Scope() with { AllowLocalProcessLaunches = true },
        });
        wallStarted.Stop();
        Assert(result.Status == PcAgentStatus.Completed && result.ElapsedMilliseconds >= 120,
            "terminal route-learning persistence was omitted from active elapsed time");
        Assert(Math.Abs(wallStarted.ElapsedMilliseconds - result.ElapsedMilliseconds) < 100,
            "reported elapsed time diverged materially from terminal wall time");
        return Task.CompletedTask;
    }

    internal static Task CorruptMemoryDoesNotTerminateRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-corrupt-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            void RunCase(string knowledgeJson, string runbookJson)
            {
                var knowledgePath = Path.Combine(directory, $"knowledge-{Guid.NewGuid():N}.json");
                var runbookPath = Path.Combine(directory, $"runbooks-{Guid.NewGuid():N}.json");
                if (knowledgeJson.Length > 0)
                {
                    File.WriteAllText(knowledgePath, knowledgeJson);
                }

                if (runbookJson.Length > 0)
                {
                    File.WriteAllText(runbookPath, runbookJson);
                }

                using var provider = new RecordingProvider(
                [
                    new FinishDecision("Completed without local memory.", AgentWorkingState.Empty, "Visible fixture"),
                ]);
                using var desktop = new FakeDesktop([Observation(1, [1])]);
                var routes = new PcLocalRouteRuntime(
                    new PcKnowledgeStore(knowledgePath),
                    new PcRunbookStore(runbookPath));
                using var loop = new PcAgentLoop(
                    desktop,
                    provider,
                    Options(),
                    new FixedWindowInspector(),
                    localRoutes: routes);

                var result = loop.Run(Request(maxNoProgress: 3));
                Assert(result.Status == PcAgentStatus.Completed && result.ModelTurns == 1,
                    "malformed local memory terminated the visible PC run");
            }

            RunCase("{\"version\":1,\"entries\":null}", "");
            RunCase("", "{\"version\":1,\"runbooks\":null}");
            RunCase("{\"version\":1,\"entries\":[null]}", "");
            RunCase("", "{\"version\":1,\"runbooks\":[null]}");
            RunCase("", "{\"version\":1,\"runbooks\":[{\"key\":\"workflow.fixture\",\"title\":\"Fixture\",\"summary\":\"Fixture route\",\"searchTerms\":[\"fixture route\"],\"steps\":[{\"id\":\"status\",\"kind\":\"local_http\",\"description\":\"Read fixture.\",\"executablePath\":\"\",\"workingDirectory\":\"\",\"expectedForegroundProcess\":\"\",\"expectedWindowTitle\":\"\",\"effect\":\"none\",\"httpMethod\":\"GET\",\"httpUrl\":\"http://127.0.0.1:1/status\",\"httpBody\":\"\",\"requiresElevation\":false,\"requiredBeforeFinish\":false,\"arguments\":[],\"timeoutMilliseconds\":10000}],\"source\":\"manual\",\"confidence\":100,\"createdUtc\":\"2026-01-01T00:00:00Z\",\"updatedUtc\":\"2026-01-01T00:00:00Z\",\"revision\":1,\"stepPerformance\":{\"status\":null}}]}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static Task FastStartRejectsCompetingBrowser()
    {
        var chrome = new PcLaunchCoordinator.PcLaunchWindow((nint)1, "chrome");
        var edge = new PcLaunchCoordinator.PcLaunchWindow((nint)2, "msedge");
        var existing = new HashSet<nint>([(nint)1, (nint)2]);
        var wrongForeground = PcLaunchCoordinator.SelectLaunchTarget(
            [chrome, edge],
            existing,
            (nint)1,
            (nint)1,
            "msedge");
        Assert(wrongForeground == IntPtr.Zero,
            "fast start selected a pre-existing competing Chrome window for an Edge URL launch");

        var switchedToHandler = PcLaunchCoordinator.SelectLaunchTarget(
            [chrome, edge],
            existing,
            (nint)1,
            (nint)2,
            "msedge");
        Assert(switchedToHandler == (nint)2, "fast start did not accept the actual foreground handler");

        var newEdge = new PcLaunchCoordinator.PcLaunchWindow((nint)3, "msedge");
        var spawnedHandler = PcLaunchCoordinator.SelectLaunchTarget(
            [chrome, edge, newEdge],
            existing,
            (nint)1,
            (nint)1,
            "msedge");
        Assert(spawnedHandler == (nint)3, "fast start did not prefer the newly spawned handler window");

        var unrelatedChrome = new PcLaunchCoordinator.PcLaunchWindow((nint)4, "chrome");
        var competingNewWindow = PcLaunchCoordinator.SelectLaunchTarget(
            [chrome, edge, unrelatedChrome],
            existing,
            (nint)1,
            (nint)1,
            "msedge");
        Assert(competingNewWindow == IntPtr.Zero,
            "fast start accepted a newly created window from the wrong browser family");
        return Task.CompletedTask;
    }

    internal static Task McpRunbookSchemaMatchesHandler()
    {
        var definitions = PcRunbookTools.Definitions();
        var update = JsonSerializer.SerializeToElement(definitions)
            .EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "pc_runbook_update");
        var variants = update.GetProperty("inputSchema").GetProperty("oneOf");
        Assert(variants.GetArrayLength() == 2, "runbook update schema does not discriminate upsert and forget");
        var upsert = variants.EnumerateArray().Single(variant =>
            variant.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "upsert");
        var required = upsert.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet();
        Assert(required.IsSupersetOf(["operation", "key", "title", "summary", "search_terms", "steps", "source"]),
            "schema-valid runbook upserts can omit handler-required fields");
        var step = upsert.GetProperty("properties").GetProperty("steps").GetProperty("items");
        Assert(step.GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
            "runbook steps allow unbounded extra command fields");
        Assert(step.GetProperty("properties").TryGetProperty("required_before_finish", out var requiredVerifier) &&
               requiredVerifier.GetProperty("type").GetString() == "boolean",
            "runbook schema omits the driver-enforced read-only finish verifier flag");
        var kinds = step.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToHashSet();
        Assert(kinds.Contains("process") &&
               step.GetProperty("properties").TryGetProperty("arguments", out var arguments) &&
               arguments.GetProperty("maxItems").GetInt32() == PcRunbookStore.MaxProcessArguments &&
               step.GetProperty("properties").TryGetProperty("timeout_ms", out _),
            "runbook schema omits bounded fixed direct-process commands");
        return Task.CompletedTask;
    }

    internal static Task RunbookLocalInterfacesAreLoopbackAndAttributed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rapid-pc-runbook-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json"));
            void Upsert(PcRunbookStep step) => store.Upsert(
                "workflow.local-http-fixture",
                "Local app fixture",
                "Exercise one exact trusted local app interface.",
                ["local fixture"],
                [step],
                "user",
                100);

            Expect<ArgumentException>(() => Upsert(new PcRunbookStep(
                "remote", "local_http", "Do not contact a remote service.", "", "", "",
                Effect: "local_deletion", HttpMethod: "POST", HttpUrl: "http://example.com/reset", HttpBody: "{}")));
            Expect<ArgumentException>(() => Upsert(new PcRunbookStep(
                "unattributed", "local_http", "Do not mutate without authority.", "", "", "",
                HttpMethod: "POST", HttpUrl: "http://127.0.0.1:8787/reset", HttpBody: "{}")));
            Expect<ArgumentException>(() => Upsert(new PcRunbookStep(
                "mutating-get", "local_http", "GET must remain read-only.", "", "", "",
                Effect: "local_deletion", HttpMethod: "GET", HttpUrl: "http://localhost:8787/status")));
            Expect<ArgumentException>(() => Upsert(new PcRunbookStep(
                "required-post", "local_http", "A mutation cannot satisfy the finish verifier.", "", "", "",
                Effect: "local_deletion", HttpMethod: "POST", HttpUrl: "http://localhost:8787/reset", HttpBody: "{}",
                RequiredBeforeFinish: true)));
            Expect<ArgumentException>(() => Upsert(new PcRunbookStep(
                "required-process", "process", "A process cannot bypass launch authority at finish.",
                @"C:\fixture.exe", @"C:\", "",
                RequiredBeforeFinish: true,
                TimeoutMilliseconds: 5_000)));

            Upsert(new PcRunbookStep(
                "status", "local_http", "Read bounded local status.", "", "", "",
                HttpMethod: "GET", HttpUrl: "http://127.0.0.1:8787/status", RequiredBeforeFinish: true));
            var runtime = new PcLocalRouteRuntime(
                new PcKnowledgeStore(Path.Combine(directory, "knowledge-v1.json")),
                new PcRunbookStore(Path.Combine(directory, "runbooks-v1.json")));
            var retrieval = runtime.Search("local fixture");
            Assert(retrieval.Context.Contains("required read-only local app verifier status", StringComparison.Ordinal),
                "the safe local app step was not projected by opaque ID");
            Assert(!retrieval.Context.Contains("127.0.0.1", StringComparison.Ordinal),
                "the local endpoint leaked into inner-model context");
            Assert(retrieval.RequiredSteps.Contains(new PcRunbookStepReference("workflow.local-http-fixture", "status")),
                "the exact read-only finish verifier was not projected into driver state");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }


    private static string TestDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("RAPID_PC_TEST_DOTNET_HOST");
        if (!string.IsNullOrWhiteSpace(configuredHost) && File.Exists(configuredHost))
        {
            return Path.GetFullPath(configuredHost);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var root = Path.Combine(directory.FullName, ".tools", "dotnet");
            if (!Directory.Exists(root))
            {
                continue;
            }

            var host = Directory.GetFiles(root, "dotnet.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (host is not null)
            {
                return host;
            }
        }

        throw new InvalidOperationException("The verification-selected .NET host is unavailable.");
    }

    private static PcAgentOptions Options() => new(
        Enabled: true,
        Provider: "openai",
        Model: "gpt-5.6-luna",
        ReasoningEffort: "low",
        ServiceTier: "fast",
        MaxModelTurns: 48,
        MaxActions: 96,
        MaxDurationMilliseconds: 120_000,
        MaxConsecutiveNoProgressTurns: 3,
        ImageDetail: "original");

    private static PcRunScope Scope() => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AllowExternalCommunication: false,
        AllowRemoteContentChanges: false,
        AllowLocalDeletion: false,
        AllowCredentials: true,
        AllowPurchases: false,
        AllowAccountOrPermissionChanges: false);

    private static PcRunRequest Request(int maxNoProgress) => new(
        "Complete the harmless fixture.",
        Scope(),
        new PcRunLimits(12, 32, 30_000, maxNoProgress),
        ReturnFinalScreenshot: false);

    private static Observation Observation(long frameId, byte[] bytes)
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Expect<TException>(Action action)
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

}

internal sealed class FakeLocalRouteRuntime(
    string runbookKey,
    string stepId,
    bool returnMatch = true,
    string stepKind = "launch",
    string effect = "none",
    bool requiresElevation = false,
    bool requiredBeforeFinish = false,
    bool activateMatch = true,
    bool throwAfterDispatch = false,
    string resultContext = "",
    int recordPerformanceDelayMilliseconds = 0) : IPcLocalRouteRuntime
{
    internal List<string> SearchQueries { get; } = [];
    internal List<string> Executions { get; } = [];
    internal List<PcRunbookExecutionSample> PerformanceSamples { get; } = [];

    public PcRouteRetrievalResult Search(string query)
    {
        SearchQueries.Add(query);
        if (!returnMatch)
        {
            return new PcRouteRetrievalResult(
                "No match.",
                new HashSet<string>(),
                new HashSet<string>(),
                new Dictionary<PcRunbookStepReference, PcRunbookStep>(),
                new HashSet<PcRunbookStepReference>(),
                0,
                0,
                10);
        }

        var reference = new PcRunbookStepReference(runbookKey, stepId);
        var step = Step(reference);
        return new PcRouteRetrievalResult(
            $"Trusted runbooks:\n- runbook {runbookKey}\n  - executable step {stepId}: Open fixture.",
            new HashSet<string>([runbookKey], StringComparer.Ordinal),
            activateMatch
                ? new HashSet<string>([runbookKey], StringComparer.Ordinal)
                : new HashSet<string>(),
            new Dictionary<PcRunbookStepReference, PcRunbookStep> { [reference] = step },
            requiredBeforeFinish
                ? new HashSet<PcRunbookStepReference>([reference])
                : new HashSet<PcRunbookStepReference>(),
            0,
            1,
            10);
    }

    private PcRunbookStep Step(PcRunbookStepReference reference) => new(
            reference.StepId,
            stepKind,
            "Open fixture.",
            stepKind == "launch" ? @"C:\Fixture.exe" : "",
            stepKind == "launch" ? @"C:\" : "",
            stepKind == "launch" ? "fixture" : "",
            Effect: effect,
            HttpMethod: stepKind == "local_http" ? (requiredBeforeFinish ? "GET" : "POST") : "",
            HttpUrl: stepKind == "local_http" ? "http://127.0.0.1:1/fixture" : "",
            HttpBody: stepKind == "local_http" && !requiredBeforeFinish ? "{}" : "",
            RequiresElevation: requiresElevation,
            RequiredBeforeFinish: requiredBeforeFinish);

    public PcRunbookExecutionResult Execute(
        PcRunbookStepReference reference,
        PcRunbookStep selectedStep,
        Action checkOperation)
    {
        checkOperation();
        if (reference.RunbookKey != runbookKey || reference.StepId != stepId || selectedStep != Step(reference))
        {
            throw new InvalidOperationException("The fixture received the wrong trusted runbook step.");
        }

        Executions.Add($"{reference.RunbookKey}/{reference.StepId}");
        if (throwAfterDispatch)
        {
            throw new HttpRequestException("Fixture failed after dispatch.");
        }

        return new PcRunbookExecutionResult(
            reference.RunbookKey,
            reference.StepId,
            "Open fixture.",
            "fixture",
            true,
            10,
            20,
            30,
            ResultContext: resultContext,
            StepKind: stepKind);
    }

    public void RecordPerformance(IReadOnlyList<PcRunbookExecutionSample> samples)
    {
        if (recordPerformanceDelayMilliseconds > 0)
        {
            Thread.Sleep(recordPerformanceDelayMilliseconds);
        }

        PerformanceSamples.AddRange(samples);
    }
}
