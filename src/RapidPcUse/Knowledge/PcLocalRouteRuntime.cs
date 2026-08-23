using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace RapidPcUse.Knowledge;

internal interface IPcLocalRouteRuntime
{
    PcRouteRetrievalResult Search(string query);

    PcRunbookExecutionResult Execute(
        PcRunbookStepReference reference,
        PcRunbookStep step,
        Action checkOperation);

    void RecordPerformance(IReadOnlyList<PcRunbookExecutionSample> samples);
}

internal sealed class PcLocalRouteRuntime(
    PcKnowledgeStore? knowledgeStore = null,
    PcRunbookStore? runbookStore = null) : IPcLocalRouteRuntime
{
    private const int MaximumReadinessWaitMilliseconds = 2_500;
    private const int ReadinessPollMilliseconds = 25;
    private readonly PcKnowledgeStore _knowledgeStore = knowledgeStore ?? new PcKnowledgeStore();
    private readonly PcRunbookStore _runbookStore = runbookStore ?? new PcRunbookStore();

    public PcRouteRetrievalResult Search(string query)
    {
        var started = Stopwatch.GetTimestamp();
        var knowledge = _knowledgeStore.Search(query, 4);
        var runbooks = _runbookStore.Search(query, 4);
        var context = BuildContext(knowledge, runbooks);
        return new PcRouteRetrievalResult(
            context.Text,
            context.IncludedRunbookKeys,
            runbooks
                .Take(1)
                .Where(runbook => context.IncludedRunbookKeys.Contains(runbook.Key) && IsStrongTaskMatch(runbook, query))
                .Select(runbook => runbook.Key)
                .ToHashSet(StringComparer.Ordinal),
            context.ExecutableSteps,
            context.ExecutableSteps
                .Where(entry => entry.Value.RequiredBeforeFinish)
                .Select(entry => entry.Key)
                .ToHashSet(),
            knowledge.Count,
            runbooks.Count,
            ElapsedMicroseconds(started));
    }

    private static bool IsStrongTaskMatch(PcRunbook runbook, string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0)
        {
            return false;
        }

        return new[] { runbook.Title }.Concat(runbook.SearchTerms).Any(candidate =>
        {
            candidate = candidate.Trim();
            return candidate.Length >= 8 &&
                candidate.Any(char.IsWhiteSpace) &&
                normalizedQuery.Contains(candidate, StringComparison.OrdinalIgnoreCase);
        });
    }

    public PcRunbookExecutionResult Execute(
        PcRunbookStepReference reference,
        PcRunbookStep step,
        Action checkOperation)
    {
        if (step.Kind == "local_http")
        {
            return ExecuteLocalHttp(reference, step, checkOperation);
        }

        if (step.Kind == "process")
        {
            return ExecuteProcess(reference, step, checkOperation);
        }

        if (step.Kind != "launch")
        {
            throw new InvalidOperationException("Only trusted executable runbook steps can be executed by the local route runtime.");
        }

        if (!File.Exists(step.ExecutablePath))
        {
            throw new FileNotFoundException("The trusted runbook launch target is unavailable.", step.ExecutablePath);
        }

        if (step.RequiresElevation)
        {
            throw new PcRunbookRequiresElevationException();
        }

        checkOperation();
        var totalStarted = Stopwatch.GetTimestamp();
        if (!string.IsNullOrWhiteSpace(step.ExpectedForegroundProcess) &&
            TryActivateProcessWindow(step.ExpectedForegroundProcess, step.ExpectedWindowTitle))
        {
            var reusedCompleted = Stopwatch.GetTimestamp();
            return new PcRunbookExecutionResult(
                reference.RunbookKey,
                reference.StepId,
                step.Description,
                ForegroundProcessName(),
                true,
                0,
                ElapsedMicroseconds(totalStarted, reusedCompleted),
                ElapsedMicroseconds(totalStarted, reusedCompleted),
                ReusedExistingTarget: true,
                StepKind: step.Kind);
        }

        var dispatchStarted = Stopwatch.GetTimestamp();
        using (Process.Start(new ProcessStartInfo(step.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = step.WorkingDirectory,
        }) ?? throw new InvalidOperationException("Windows did not start the trusted runbook target."))
        {
        }

        var dispatchCompleted = Stopwatch.GetTimestamp();
        var targetObserved = string.IsNullOrWhiteSpace(step.ExpectedForegroundProcess);
        var readinessStarted = dispatchCompleted;
        while (!targetObserved && Stopwatch.GetElapsedTime(readinessStarted).TotalMilliseconds < MaximumReadinessWaitMilliseconds)
        {
            checkOperation();
            targetObserved = ForegroundMatches(step.ExpectedForegroundProcess, step.ExpectedWindowTitle) ||
                TryActivateProcessWindow(step.ExpectedForegroundProcess, step.ExpectedWindowTitle);
            if (!targetObserved)
            {
                Thread.Sleep(ReadinessPollMilliseconds);
            }
        }

        checkOperation();
        var completed = Stopwatch.GetTimestamp();
        return new PcRunbookExecutionResult(
            reference.RunbookKey,
            reference.StepId,
            step.Description,
            ForegroundProcessName(),
            targetObserved,
            ElapsedMicroseconds(dispatchStarted, dispatchCompleted),
            ElapsedMicroseconds(readinessStarted, completed),
            ElapsedMicroseconds(totalStarted, completed),
            StepKind: step.Kind);
    }

    public void RecordPerformance(IReadOnlyList<PcRunbookExecutionSample> samples)
        => _runbookStore.RecordPerformance(samples);

    private static PcRunbookExecutionResult ExecuteProcess(
        PcRunbookStepReference reference,
        PcRunbookStep step,
        Action checkOperation)
    {
        if (!File.Exists(step.ExecutablePath))
        {
            throw new FileNotFoundException("The trusted command target is unavailable.", step.ExecutablePath);
        }

        if (step.RequiresElevation)
        {
            throw new PcRunbookRequiresElevationException();
        }

        checkOperation();
        var totalStarted = Stopwatch.GetTimestamp();
        var startInfo = new ProcessStartInfo(step.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = step.WorkingDirectory,
        };
        SanitizeProcessEnvironment(startInfo);
        foreach (var argument in step.Arguments ?? Array.Empty<string>())
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var dispatchStarted = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows did not start the exact trusted command.");
        }

        using var containment = PcProcessContainment.Attach(process);
        process.StandardInput.Close();
        var dispatchCompleted = Stopwatch.GetTimestamp();
        using var outputCancellation = new CancellationTokenSource();
        var standardOutput = DrainBoundedAsync(process.StandardOutput, 4_096, outputCancellation.Token);
        var standardError = DrainBoundedAsync(process.StandardError, 4_096, outputCancellation.Token);
        var timedOut = false;
        try
        {
            while (!process.WaitForExit(ReadinessPollMilliseconds))
            {
                checkOperation();
                if (Stopwatch.GetElapsedTime(dispatchCompleted).TotalMilliseconds >= step.TimeoutMilliseconds)
                {
                    timedOut = true;
                    containment.TerminateAndWait(process, 2_000);
                    break;
                }
            }

            checkOperation();
        }
        catch (Exception operationException)
        {
            try
            {
                containment.TerminateAndWait(process, 2_000);
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    "The trusted command was interrupted, but its contained process job did not terminate cleanly.",
                    new AggregateException(operationException, cleanupException));
            }

            throw;
        }

        var drain = Task.WhenAll(standardOutput, standardError);
        try
        {
            if (!drain.Wait(2_000))
            {
                outputCancellation.Cancel();
                _ = drain.Wait(500);
            }
        }
        catch (AggregateException)
        {
            // Output is bounded result data, not a reason to lose a completed process result.
        }

        var completed = Stopwatch.GetTimestamp();
        if (timedOut)
        {
            if (step.Effect != "none")
            {
                return UncertainProcessResult(
                    reference,
                    step,
                    dispatchStarted,
                    dispatchCompleted,
                    completed,
                    "The exact trusted command timed out after dispatch. Its effect is uncertain: never repeat it. Use a separate read-only verifier.");
            }

            throw new TimeoutException("The exact trusted read-only command exceeded its fixed timeout.");
        }

        if (process.ExitCode != 0)
        {
            if (step.Effect != "none")
            {
                return UncertainProcessResult(
                    reference,
                    step,
                    dispatchStarted,
                    dispatchCompleted,
                    completed,
                    "The exact trusted command returned a nonzero exit code after dispatch. Its effect is uncertain: never repeat it. Use a separate read-only verifier.");
            }

            throw new InvalidOperationException("The exact trusted read-only command returned a nonzero exit code.");
        }

        var output = ProcessResultData(
            standardOutput.IsCompletedSuccessfully ? standardOutput.Result : new BoundedProcessText("", true),
            standardError.IsCompletedSuccessfully ? standardError.Result : new BoundedProcessText("", true));
        return new PcRunbookExecutionResult(
            reference.RunbookKey,
            reference.StepId,
            step.Description,
            ForegroundProcessName(),
            true,
            ElapsedMicroseconds(dispatchStarted, dispatchCompleted),
            ElapsedMicroseconds(dispatchCompleted, completed),
            ElapsedMicroseconds(totalStarted, completed),
            ResultContext: output,
            StepKind: step.Kind);
    }

    private static PcRunbookExecutionResult UncertainProcessResult(
        PcRunbookStepReference reference,
        PcRunbookStep step,
        long dispatchStarted,
        long dispatchCompleted,
        long completed,
        string resultContext)
        => new(
            reference.RunbookKey,
            reference.StepId,
            step.Description,
            ForegroundProcessName(),
            false,
            ElapsedMicroseconds(dispatchStarted, dispatchCompleted),
            ElapsedMicroseconds(dispatchCompleted, completed),
            ElapsedMicroseconds(dispatchStarted, completed),
            ResultContext: resultContext,
            StepKind: step.Kind,
            EffectUncertain: true);

    private static async Task<BoundedProcessText> DrainBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(maximumCharacters);
        var buffer = new char[512];
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var accepted = Math.Min(read, maximumCharacters - builder.Length);
                if (accepted > 0)
                {
                    builder.Append(buffer, 0, accepted);
                }

                truncated |= accepted < read;
            }
        }
        catch (OperationCanceledException)
        {
            truncated = true;
        }

        return new BoundedProcessText(builder.ToString(), truncated);
    }

    private static string ProcessResultData(BoundedProcessText output, BoundedProcessText error)
    {
        var builder = new StringBuilder();
        if (output.Text.Length > 0)
        {
            builder.Append("stdout: ").Append(output.Text.Trim());
        }

        if (error.Text.Length > 0)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append("stderr: ").Append(error.Text.Trim());
        }

        if (output.Truncated || error.Truncated)
        {
            builder.Append(" [bounded output truncated]");
        }

        return builder.Length == 0 ? "The exact trusted command completed with exit code 0 and no output." : builder.ToString();
    }

    private static void SanitizeProcessEnvironment(ProcessStartInfo startInfo)
    {
        var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                retained[name] = value;
            }
        }

        startInfo.Environment.Clear();
        foreach (var entry in retained)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }
    }

    private static PcRunbookExecutionResult ExecuteLocalHttp(
        PcRunbookStepReference reference,
        PcRunbookStep step,
        Action checkOperation)
    {
        checkOperation();
        var started = Stopwatch.GetTimestamp();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(new HttpMethod(step.HttpMethod), step.HttpUrl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (step.HttpBody.Length > 0)
        {
            request.Content = new StringContent(step.HttpBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = AwaitWithControl(
                client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token),
                checkOperation,
                timeout);
        }
        catch (TaskCanceledException) when (step.HttpMethod == "POST")
        {
            var timedOut = Stopwatch.GetTimestamp();
            return new PcRunbookExecutionResult(
                reference.RunbookKey,
                reference.StepId,
                step.Description,
                ForegroundProcessName(),
                false,
                ElapsedMicroseconds(started, timedOut),
                0,
                ElapsedMicroseconds(started, timedOut),
                ResultContext: "The trusted local POST response timed out after dispatch. Its effect is uncertain: never repeat it. Use a separate read-only verification step or visible evidence.",
                StepKind: step.Kind,
                EffectUncertain: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The trusted local app interface returned HTTP {(int)response.StatusCode}.");
            }

            using var stream = response.Content.ReadAsStream();
            var buffer = new byte[8_193];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = AwaitWithControl(
                    stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), timeout.Token).AsTask(),
                    checkOperation,
                    timeout);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > 8_192)
            {
                throw new InvalidOperationException("The trusted local app interface response exceeded its bounded result size.");
            }

            checkOperation();
            var completed = Stopwatch.GetTimestamp();
            return new PcRunbookExecutionResult(
                reference.RunbookKey,
                reference.StepId,
                step.Description,
                ForegroundProcessName(),
                true,
                ElapsedMicroseconds(started, completed),
                0,
                ElapsedMicroseconds(started, completed),
                ResultContext: Encoding.UTF8.GetString(buffer, 0, count),
                StepKind: step.Kind);
        }
    }

    private static T AwaitWithControl<T>(
        Task<T> task,
        Action checkOperation,
        CancellationTokenSource cancellation)
    {
        try
        {
            while (!task.IsCompleted)
            {
                checkOperation();
                Thread.Sleep(ReadinessPollMilliseconds);
            }

            checkOperation();
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private static PcBuiltRouteContext BuildContext(
        IReadOnlyList<PcKnowledgeEntry> knowledge,
        IReadOnlyList<PcRunbook> runbooks)
    {
        var builder = new StringBuilder();
        var executableSteps = new Dictionary<PcRunbookStepReference, PcRunbookStep>();
        var includedRunbookKeys = new HashSet<string>(StringComparer.Ordinal);

        if (runbooks.Count > 0)
        {
            builder.AppendLine("Trusted runbooks (only executable launch or local app steps may be selected):");
            foreach (var runbook in runbooks)
            {
                if (!AppendBounded(builder, $"- runbook {runbook.Key}: {runbook.Title}. {runbook.Summary}"))
                {
                    break;
                }

                includedRunbookKeys.Add(runbook.Key);
                foreach (var indexedStep in runbook.Steps
                             .Select((step, index) => new { Step = step, Index = index + 1 })
                             .OrderByDescending(item => item.Step.RequiredBeforeFinish)
                             .ThenBy(item => item.Index))
                {
                    var step = indexedStep.Step;
                    var marker = step.Kind switch
                    {
                        "launch" => $"ordered step {indexedStep.Index}, executable step {step.Id}",
                        "process" => $"ordered step {indexedStep.Index}, trusted command step {step.Id} (effect: {step.Effect})",
                        "local_http" when step.RequiredBeforeFinish => $"ordered step {indexedStep.Index}, required read-only local app verifier {step.Id}",
                        "local_http" => $"ordered step {indexedStep.Index}, trusted local app step {step.Id} (effect: {step.Effect})",
                        _ => $"ordered step {indexedStep.Index}, guidance",
                    };
                    var projectedDescription = step.Description[..Math.Min(step.Description.Length, 280)];
                    if (!AppendBounded(builder, $"  - {marker}: {projectedDescription}"))
                    {
                        break;
                    }

                    if (step.Kind is "launch" or "process" or "local_http")
                    {
                        executableSteps[new PcRunbookStepReference(runbook.Key, step.Id)] = step;
                    }
                }
            }
        }

        if (knowledge.Count > 0 && AppendBounded(builder, "Trusted local facts:"))
        {
            foreach (var entry in knowledge)
            {
                var projectedFact = entry.Fact[..Math.Min(entry.Fact.Length, 300)];
                var projectedHint = entry.NavigationHint[..Math.Min(entry.NavigationHint.Length, 240)];
                if (!AppendBounded(builder, $"- [{entry.Key}] {entry.Subject}: {projectedFact} {projectedHint}".TrimEnd()))
                {
                    break;
                }
            }
        }

        if (builder.Length == 0)
        {
            builder.Append("No matching trusted local knowledge or runbook was found. Use visible evidence or request an outer handoff.");
        }

        return new PcBuiltRouteContext(builder.ToString().TrimEnd(), includedRunbookKeys, executableSteps);
    }

    private static bool AppendBounded(StringBuilder builder, string line)
    {
        var remaining = SecurityLimits.MaxAgentRetrievedContextCharacters - builder.Length;
        if (remaining <= 1 || line.Length + Environment.NewLine.Length > remaining)
        {
            return false;
        }

        builder.AppendLine(line);
        return true;
    }

    private static bool ForegroundMatches(string expectedProcess, string expectedWindowTitle)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, expectedProcess, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(expectedWindowTitle) ||
                 process.MainWindowTitle.Contains(expectedWindowTitle, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryActivateProcessWindow(string expectedProcess, string expectedWindowTitle)
    {
        foreach (var process in Process.GetProcessesByName(expectedProcess))
        {
            using (process)
            {
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedWindowTitle) &&
                        !process.MainWindowTitle.Contains(expectedWindowTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (NativeMethods.IsIconic(window))
                    {
                        _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
                    }

                    if (PcLaunchCoordinator.TryActivateWindow(window))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // A short-lived helper can exit while its windows are enumerated.
                }
            }
        }

        return false;
    }

    private static string ForegroundProcessName() => PcLaunchCoordinator.ForegroundProcessName();

    private static long ElapsedMicroseconds(long started)
        => (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000);

    private static long ElapsedMicroseconds(long started, long completed)
        => (long)(Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds * 1_000);

    private sealed record PcBuiltRouteContext(
        string Text,
        IReadOnlySet<string> IncludedRunbookKeys,
        IReadOnlyDictionary<PcRunbookStepReference, PcRunbookStep> ExecutableSteps);

    private sealed record BoundedProcessText(string Text, bool Truncated);
}

internal sealed class PcRunbookRequiresElevationException : InvalidOperationException
{
    internal PcRunbookRequiresElevationException()
        : base("The trusted local runbook target requires Windows elevation.")
    {
    }
}
