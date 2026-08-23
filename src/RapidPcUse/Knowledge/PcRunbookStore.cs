using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RapidPcUse.Knowledge;

internal sealed class PcRunbookStore
{
    internal const int CurrentVersion = 1;
    internal const int MaxRunbooks = 64;
    internal const int MaxSteps = 16;
    internal const int MaxSearchTerms = 16;
    internal const int MaxTitleCharacters = 160;
    internal const int MaxSummaryCharacters = 1_200;
    internal const int MaxDescriptionCharacters = 600;
    internal const int MaxSearchTermCharacters = 100;
    internal const int MaxPathCharacters = 1_024;
    internal const int MaxProcessCharacters = 64;
    internal const int MaxWindowTitleCharacters = 160;
    internal const int MaxHttpUrlCharacters = 512;
    internal const int MaxHttpBodyCharacters = 2_048;
    internal const int MaxProcessArguments = 32;
    internal const int MaxProcessArgumentCharacters = 512;
    internal const int MaxProcessTimeoutMilliseconds = 30_000;
    internal const int MaxSearchResults = 6;
    private const long MaxStoreBytes = 512 * 1024;
    private static readonly TimeSpan StoreLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RouteLearningLockTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal) { "guidance", "launch", "process", "local_http" };
    private static readonly HashSet<string> AllowedEffects = new(StringComparer.Ordinal)
    {
        "none", "external_communication", "remote_content_change", "local_deletion",
    };
    private static readonly HashSet<string> AllowedSources = new(StringComparer.Ordinal)
    {
        "user", "verified_observation", "benchmark", "manual",
    };
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "are", "for", "from", "into", "that", "the", "then", "this", "with", "without",
    };
    private static readonly char[] SearchTermTrimCharacters =
        ['.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _mutexName;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    internal PcRunbookStore(string? path = null, TimeProvider? timeProvider = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath());
        _mutexName = MutexName(_path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal string PathForDiagnostics => _path;

    internal static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RapidPcUse",
        "runbooks-v1.json");

    internal IReadOnlyList<PcRunbook> Search(string query, int limit)
    {
        query = Bounded(query, nameof(query), SecurityLimits.MaxAgentRetrievalQueryCharacters, allowEmpty: true);
        limit = Math.Clamp(limit, 1, MaxSearchResults);
        return WithStoreLock(() =>
        {
            var runbooks = Load();
            if (query.Length == 0)
            {
                return (IReadOnlyList<PcRunbook>)runbooks
                    .OrderByDescending(runbook => runbook.UpdatedUtc)
                    .Take(limit)
                    .ToArray();
            }

            var terms = Terms(query);
            return (IReadOnlyList<PcRunbook>)runbooks
                .Select(runbook => new { Runbook = runbook, Score = Score(runbook, query, terms) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => PerformanceReliability(candidate.Runbook))
                .ThenBy(candidate => AverageSuccessfulMicroseconds(candidate.Runbook))
                .ThenByDescending(candidate => candidate.Runbook.Confidence)
                .ThenByDescending(candidate => candidate.Runbook.UpdatedUtc)
                .Take(limit)
                .Select(candidate => candidate.Runbook)
                .ToArray();
        });
    }

    internal PcRunbook? Find(string key)
    {
        key = ValidKey(key);
        return WithStoreLock(() => Load().SingleOrDefault(
            runbook => string.Equals(runbook.Key, key, StringComparison.Ordinal)));
    }

    internal PcRunbook Upsert(
        string key,
        string title,
        string summary,
        IReadOnlyList<string> searchTerms,
        IReadOnlyList<PcRunbookStep> steps,
        string source,
        int confidence)
    {
        key = ValidKey(key);
        title = Bounded(title, nameof(title), MaxTitleCharacters, allowEmpty: false);
        summary = Bounded(summary, nameof(summary), MaxSummaryCharacters, allowEmpty: false);
        if (searchTerms.Count > MaxSearchTerms || steps.Count is < 1 or > MaxSteps)
        {
            throw new ArgumentException("Runbook search terms or steps exceeded the configured bounds.");
        }

        var normalizedTerms = searchTerms
            .Select(term => Bounded(term, "search term", MaxSearchTermCharacters, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedSteps = steps.Select(ValidateStep).ToArray();
        if (normalizedSteps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != normalizedSteps.Length)
        {
            throw new ArgumentException("Runbook step IDs must be unique.");
        }

        if (normalizedSteps.Count(step => step.RequiredBeforeFinish) > 4)
        {
            throw new ArgumentException("A runbook can require at most four exact read-only verification steps.");
        }

        source = Allowed(source, nameof(source), AllowedSources);
        if (confidence is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        return WithStoreLock(() =>
        {
            var runbooks = Load();
            var index = runbooks.FindIndex(runbook => string.Equals(runbook.Key, key, StringComparison.Ordinal));
            if (index < 0 && runbooks.Count >= MaxRunbooks)
            {
                throw new InvalidOperationException($"The PC runbook store is limited to {MaxRunbooks} runbooks.");
            }

            var now = _timeProvider.GetUtcNow();
            var previous = index < 0 ? null : runbooks[index];
            var stepsById = normalizedSteps.ToDictionary(step => step.Id, StringComparer.Ordinal);
            var preservedPerformance = previous?.StepPerformance?
                .Where(entry => entry.Value is not null &&
                    stepsById.TryGetValue(entry.Key, out var step) &&
                    string.Equals(entry.Value.StepFingerprint, ExecutionFingerprint(step), StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            var runbook = new PcRunbook(
                key,
                title,
                summary,
                normalizedTerms,
                normalizedSteps,
                source,
                confidence,
                previous?.CreatedUtc ?? now,
                now,
                (previous?.Revision ?? 0) + 1,
                preservedPerformance);
            if (index < 0)
            {
                runbooks.Add(runbook);
            }
            else
            {
                runbooks[index] = runbook;
            }

            Save(runbooks);
            return runbook;
        });
    }

    internal bool Forget(string key)
    {
        key = ValidKey(key);
        return WithStoreLock(() =>
        {
            var runbooks = Load();
            var removed = runbooks.RemoveAll(runbook => string.Equals(runbook.Key, key, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save(runbooks);
            }

            return removed;
        });
    }

    internal void RecordPerformance(IReadOnlyList<PcRunbookExecutionSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        _ = WithStoreLock(() =>
        {
            var runbooks = Load();
            var changed = false;
            foreach (var sample in samples)
            {
                var index = runbooks.FindIndex(runbook =>
                    string.Equals(runbook.Key, sample.Reference.RunbookKey, StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                var runbook = runbooks[index];
                var currentStep = runbook.Steps.FirstOrDefault(step =>
                    string.Equals(step.Id, sample.Reference.StepId, StringComparison.Ordinal));
                if (currentStep is null ||
                    !string.Equals(sample.StepFingerprint, ExecutionFingerprint(currentStep), StringComparison.Ordinal))
                {
                    // A runbook may have been revised while execution was in flight.
                    // Never attribute an old target's outcome to its replacement.
                    continue;
                }

                var performance = (runbook.StepPerformance ?? new Dictionary<string, PcRunbookStepPerformance>())
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                performance.TryGetValue(sample.Reference.StepId, out var current);
                current ??= new PcRunbookStepPerformance(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    DateTimeOffset.MinValue,
                    sample.StepFingerprint);
                var elapsed = Math.Clamp(sample.ElapsedMicroseconds, 0, 300_000_000);
                var succeeded = sample.Disposition == PcRunbookExecutionDisposition.Success;
                performance[sample.Reference.StepId] = current with
                {
                    Attempts = SaturatingIncrement(current.Attempts),
                    Successes = succeeded ? SaturatingIncrement(current.Successes) : current.Successes,
                    Failures = sample.Disposition == PcRunbookExecutionDisposition.Failure
                        ? SaturatingIncrement(current.Failures)
                        : current.Failures,
                    UncertainEffects = sample.Disposition == PcRunbookExecutionDisposition.EffectUncertain
                        ? SaturatingIncrement(current.UncertainEffects)
                        : current.UncertainEffects,
                    TotalSuccessfulMicroseconds = succeeded
                        ? SaturatingAdd(current.TotalSuccessfulMicroseconds, elapsed)
                        : current.TotalSuccessfulMicroseconds,
                    LastElapsedMicroseconds = elapsed,
                    LastAttemptUtc = _timeProvider.GetUtcNow(),
                };
                runbooks[index] = runbook with { StepPerformance = performance };
                changed = true;
            }

            if (changed)
            {
                Save(runbooks);
            }

            return changed;
        }, RouteLearningLockTimeout);
    }

    private List<PcRunbook> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var file = new FileInfo(_path);
        if (file.Length > MaxStoreBytes)
        {
            throw new InvalidOperationException("The PC runbook store exceeds its size limit.");
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = JsonSerializer.Deserialize<PcRunbookDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The PC runbook store is empty or invalid.");
        if (document.Runbooks is null ||
            document.Version != CurrentVersion ||
            document.Runbooks.Count > MaxRunbooks)
        {
            throw new InvalidOperationException("The PC runbook store version or count is invalid.");
        }

        var runbooks = document.Runbooks.ToList();
        if (runbooks.Any(runbook => runbook is null))
        {
            throw new InvalidOperationException("The PC runbook store contains a null runbook.");
        }

        if (runbooks.Select(runbook => runbook.Key).Distinct(StringComparer.Ordinal).Count() != runbooks.Count)
        {
            throw new InvalidOperationException("The PC runbook store contains duplicate keys.");
        }

        for (var index = 0; index < runbooks.Count; index++)
        {
            var runbook = runbooks[index];
            if (runbook.Steps is null || runbook.Steps.Any(step => step is null))
            {
                throw new InvalidOperationException("A stored runbook is missing a step.");
            }

            var normalizedSteps = runbook.Steps.Select(step => ValidateStep(step with
            {
                Arguments = step.Arguments ?? Array.Empty<string>(),
                TimeoutMilliseconds = step.TimeoutMilliseconds == 0 ? 10_000 : step.TimeoutMilliseconds,
            })).ToArray();
            var normalizedById = normalizedSteps.ToDictionary(step => step.Id, StringComparer.Ordinal);
            var normalizedPerformance = (runbook.StepPerformance ?? new Dictionary<string, PcRunbookStepPerformance>())
                .Where(entry => entry.Value is not null &&
                    normalizedById.TryGetValue(entry.Key, out var step) &&
                    string.Equals(entry.Value.StepFingerprint, ExecutionFingerprint(step), StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            runbooks[index] = UpsertValidationOnly(runbook with
            {
                Steps = normalizedSteps,
                StepPerformance = normalizedPerformance,
            });
        }

        return runbooks;
    }

    private static PcRunbook UpsertValidationOnly(PcRunbook runbook)
    {
        if (runbook is null || runbook.SearchTerms is null || runbook.Steps is null)
        {
            throw new InvalidOperationException("A stored runbook is missing required fields.");
        }

        _ = ValidKey(runbook.Key);
        _ = Bounded(runbook.Title, nameof(runbook.Title), MaxTitleCharacters, allowEmpty: false);
        _ = Bounded(runbook.Summary, nameof(runbook.Summary), MaxSummaryCharacters, allowEmpty: false);
        if (runbook.SearchTerms.Count > MaxSearchTerms || runbook.Steps.Count is < 1 or > MaxSteps)
        {
            throw new InvalidOperationException("A stored runbook exceeds its collection bounds.");
        }

        foreach (var term in runbook.SearchTerms)
        {
            _ = Bounded(term, "search term", MaxSearchTermCharacters, allowEmpty: false);
        }

        foreach (var step in runbook.Steps)
        {
            _ = ValidateStep(step);
        }

        if (runbook.Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != runbook.Steps.Count)
        {
            throw new InvalidOperationException("A stored runbook contains duplicate step IDs.");
        }

        if (runbook.Steps.Count(step => step.RequiredBeforeFinish) > 4)
        {
            throw new InvalidOperationException("A stored runbook requires too many finish-verification steps.");
        }

        _ = Allowed(runbook.Source, nameof(runbook.Source), AllowedSources);
        if (runbook.Confidence is < 0 or > 100 || runbook.Revision < 1)
        {
            throw new InvalidOperationException("A stored runbook has invalid metadata.");
        }

        var performance = runbook.StepPerformance ?? new Dictionary<string, PcRunbookStepPerformance>();
        if (performance.Count > runbook.Steps.Count || performance.Keys.Any(key =>
                !runbook.Steps.Any(step => string.Equals(step.Id, key, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("A stored runbook contains performance for an unknown step.");
        }

        foreach (var entry in performance)
        {
            var metric = entry.Value;
            var step = runbook.Steps.First(candidate => string.Equals(candidate.Id, entry.Key, StringComparison.Ordinal));
            if (metric is null || metric.Attempts < 0 || metric.Successes < 0 || metric.Failures < 0 ||
                metric.UncertainEffects < 0 ||
                (long)metric.Successes + metric.Failures + metric.UncertainEffects > metric.Attempts ||
                metric.TotalSuccessfulMicroseconds < 0 || metric.LastElapsedMicroseconds < 0 ||
                !string.Equals(metric.StepFingerprint, ExecutionFingerprint(step), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A stored runbook contains invalid performance metrics.");
            }
        }

        return runbook;
    }

    private static PcRunbookStep ValidateStep(PcRunbookStep step)
    {
        if (step is null)
        {
            throw new InvalidOperationException("A stored runbook contains a null step.");
        }

        var id = Bounded(step.Id, nameof(step.Id), SecurityLimits.MaxAgentRunbookStepIdCharacters, allowEmpty: false);
        var kind = Allowed(step.Kind, nameof(step.Kind), AllowedKinds);
        var description = Bounded(step.Description, nameof(step.Description), MaxDescriptionCharacters, allowEmpty: false);
        var executablePath = Bounded(step.ExecutablePath, nameof(step.ExecutablePath), MaxPathCharacters, allowEmpty: kind is not ("launch" or "process"));
        var workingDirectory = Bounded(step.WorkingDirectory, nameof(step.WorkingDirectory), MaxPathCharacters, allowEmpty: true);
        var expectedProcess = Bounded(step.ExpectedForegroundProcess, nameof(step.ExpectedForegroundProcess), MaxProcessCharacters, allowEmpty: true);
        var expectedTitle = Bounded(step.ExpectedWindowTitle, nameof(step.ExpectedWindowTitle), MaxWindowTitleCharacters, allowEmpty: true);
        var effect = Allowed(step.Effect, nameof(step.Effect), AllowedEffects);
        var httpMethod = Bounded(step.HttpMethod, nameof(step.HttpMethod), 8, allowEmpty: true).ToUpperInvariant();
        var httpUrl = Bounded(step.HttpUrl, nameof(step.HttpUrl), MaxHttpUrlCharacters, allowEmpty: true);
        var httpBody = Bounded(step.HttpBody, nameof(step.HttpBody), MaxHttpBodyCharacters, allowEmpty: true);
        var arguments = (step.Arguments ?? Array.Empty<string>())
            .Select(argument => Bounded(argument, "process argument", MaxProcessArgumentCharacters, allowEmpty: true))
            .ToArray();
        if (arguments.Length > MaxProcessArguments)
        {
            throw new ArgumentException("A trusted process step contains too many fixed arguments.");
        }

        if (step.TimeoutMilliseconds is < 1_000 or > MaxProcessTimeoutMilliseconds)
        {
            throw new ArgumentException($"A trusted process timeout must be from 1000 to {MaxProcessTimeoutMilliseconds} milliseconds.");
        }

        if (kind != "process" && step.TimeoutMilliseconds != 10_000)
        {
            throw new ArgumentException("Only trusted command steps can customize a process timeout.");
        }

        if (kind == "guidance" && (executablePath.Length > 0 || workingDirectory.Length > 0 || expectedProcess.Length > 0 || expectedTitle.Length > 0 ||
            effect != "none" || httpMethod.Length > 0 || httpUrl.Length > 0 || httpBody.Length > 0 || arguments.Length > 0 ||
            step.RequiresElevation || step.RequiredBeforeFinish))
        {
            throw new ArgumentException("Guidance steps cannot contain executable, HTTP, effect, or elevation fields.");
        }

        if (kind == "launch")
        {
            executablePath = Path.GetFullPath(executablePath);
            if (Path.GetExtension(executablePath).ToLowerInvariant() is not (".exe" or ".cmd" or ".bat" or ".lnk"))
            {
                throw new ArgumentException("Runbook launch steps support only .exe, .cmd, .bat, or .lnk targets.");
            }

            workingDirectory = workingDirectory.Length == 0
                ? Path.GetDirectoryName(executablePath) ?? ""
                : Path.GetFullPath(workingDirectory);
            if (httpMethod.Length > 0 || httpUrl.Length > 0 || httpBody.Length > 0 || effect != "none")
            {
                throw new ArgumentException("Launch steps cannot contain local HTTP or effect fields.");
            }

            if (arguments.Length > 0)
            {
                throw new ArgumentException("Visible launch steps cannot contain process arguments.");
            }
        }

        if (kind == "process")
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Trusted command steps execute an exact .exe directly without a shell.");
            }

            workingDirectory = workingDirectory.Length == 0
                ? Path.GetDirectoryName(executablePath) ?? ""
                : Path.GetFullPath(workingDirectory);
            if (expectedProcess.Length > 0 || expectedTitle.Length > 0 || httpMethod.Length > 0 || httpUrl.Length > 0 || httpBody.Length > 0)
            {
                throw new ArgumentException("Trusted command steps cannot contain foreground-window or local HTTP fields.");
            }
        }

        if (kind == "local_http")
        {
            if (executablePath.Length > 0 || workingDirectory.Length > 0 || expectedProcess.Length > 0 || expectedTitle.Length > 0 ||
                arguments.Length > 0 || step.RequiresElevation)
            {
                throw new ArgumentException("Local HTTP steps cannot contain executable or elevation fields.");
            }

            if (httpMethod is not ("GET" or "POST") ||
                !Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                uri.UserInfo.Length > 0 ||
                uri.Host is not ("127.0.0.1" or "localhost"))
            {
                throw new ArgumentException("Local HTTP steps support only exact GET/POST loopback HTTP endpoints without credentials.");
            }

            if (httpMethod == "GET" && (httpBody.Length > 0 || effect != "none"))
            {
                throw new ArgumentException("Read-only local GET steps cannot contain a body or effect.");
            }

            if (httpMethod == "POST" && effect == "none")
            {
                throw new ArgumentException("A local POST step must declare its authority effect.");
            }
        }

        if (step.RequiredBeforeFinish &&
            !(kind == "local_http" && httpMethod == "GET" && effect == "none"))
        {
            throw new ArgumentException("Only an exact read-only local GET can be required before finish.");
        }

        return new PcRunbookStep(
            id,
            kind,
            description,
            executablePath,
            workingDirectory,
            expectedProcess,
            expectedTitle,
            effect,
            httpMethod,
            httpUrl,
            httpBody,
            step.RequiresElevation,
            step.RequiredBeforeFinish,
            arguments,
            step.TimeoutMilliseconds);
    }

    private void Save(IReadOnlyList<PcRunbook> runbooks)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Runbook path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new PcRunbookDocument(CurrentVersion, runbooks.OrderBy(runbook => runbook.Key, StringComparer.Ordinal).ToArray()),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaxStoreBytes)
                {
                    throw new InvalidOperationException("The proposed PC runbook update exceeds the store size limit.");
                }
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private T WithStoreLock<T>(Func<T> operation, TimeSpan? timeout = null)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(timeout ?? StoreLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new IOException("Timed out waiting for the PC runbook store lock.");
                }

                return operation();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private static int Score(PcRunbook runbook, string query, string[] terms)
    {
        var searchable = string.Join(' ', new[] { runbook.Key, runbook.Title, runbook.Summary }
            .Concat(runbook.SearchTerms)
            .Concat(runbook.Steps.Select(step => step.Description)));
        var score = searchable.Contains(query, StringComparison.OrdinalIgnoreCase) ? 40 : 0;
        var matchingTerms = terms.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (terms.Length <= 1 || matchingTerms >= 2)
        {
            score += matchingTerms * 8;
        }

        return score;
    }

    private static double PerformanceReliability(PcRunbook runbook)
    {
        var metrics = (runbook.StepPerformance ?? new Dictionary<string, PcRunbookStepPerformance>()).Values;
        var attempts = metrics.Sum(metric => (long)metric.Attempts);
        var successes = metrics.Sum(metric => (long)metric.Successes);
        return (successes + 1d) / (attempts + 2d);
    }

    private static double AverageSuccessfulMicroseconds(PcRunbook runbook)
    {
        var metrics = (runbook.StepPerformance ?? new Dictionary<string, PcRunbookStepPerformance>()).Values;
        var successes = metrics.Sum(metric => (long)metric.Successes);
        return successes == 0
            ? double.MaxValue
            : metrics.Sum(metric => (double)metric.TotalSuccessfulMicroseconds) / successes;
    }

    private static int SaturatingIncrement(int value) => value == int.MaxValue ? value : value + 1;

    internal static string ExecutionFingerprint(PcRunbookStep step)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            step.Kind,
            step.ExecutablePath,
            step.WorkingDirectory,
            step.ExpectedForegroundProcess,
            step.ExpectedWindowTitle,
            step.Effect,
            step.HttpMethod,
            step.HttpUrl,
            step.HttpBody,
            step.RequiresElevation,
            step.RequiredBeforeFinish,
            Arguments = step.Arguments ?? Array.Empty<string>(),
            step.TimeoutMilliseconds,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string[] Terms(string value) => value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(term => term.Trim(SearchTermTrimCharacters))
        .Where(term => term.Length >= 3 && !SearchStopWords.Contains(term))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ValidKey(string value)
    {
        value = Bounded(value, nameof(value), SecurityLimits.MaxAgentRunbookKeyCharacters, allowEmpty: false);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("Runbook keys may contain only ASCII letters, numbers, dash, underscore, dot, or colon.");
        }

        return value;
    }

    private static string Bounded(string value, string name, int maximum, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} is missing or exceeds its configured bound.");
        }

        return value.Trim();
    }

    private static string Allowed(string value, string name, HashSet<string> allowed)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{name} has an unsupported value.");
        }

        return value;
    }

    private static string MutexName(string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return $"Local\\RapidPcUseRunbooks-{Convert.ToHexString(digest)}";
    }
}
