using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RapidPcUse.Knowledge;

internal sealed class PcKnowledgeStore
{
    internal const int CurrentVersion = 1;
    internal const int MaxEntries = 256;
    internal const int MaxKeyCharacters = 96;
    internal const int MaxQueryCharacters = 240;
    internal const int MaxSubjectCharacters = 200;
    internal const int MaxFactCharacters = 600;
    internal const int MaxNavigationHintCharacters = 600;
    internal const int MaxSearchResults = 12;
    private const long MaxStoreBytes = 512 * 1024;
    private static readonly TimeSpan StoreLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "app", "person", "device", "workflow", "preference", "other",
    };
    private static readonly HashSet<string> AllowedSources = new(StringComparer.Ordinal)
    {
        "user", "verified_observation", "benchmark", "manual",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _mutexName;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    internal PcKnowledgeStore(string? path = null, TimeProvider? timeProvider = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath());
        _mutexName = MutexName(_path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal string PathForDiagnostics => _path;

    internal static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RapidPcUse",
        "knowledge-v1.json");

    internal IReadOnlyList<PcKnowledgeEntry> Search(string query, int limit)
    {
        query = Bounded(query, nameof(query), MaxQueryCharacters, allowEmpty: true);
        limit = Math.Clamp(limit, 1, MaxSearchResults);
        return WithStoreLock(() =>
        {
            var entries = Load();
            if (query.Length == 0)
            {
                return (IReadOnlyList<PcKnowledgeEntry>)entries
                    .OrderByDescending(entry => entry.UpdatedUtc)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray();
            }

            var terms = query.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (IReadOnlyList<PcKnowledgeEntry>)entries
                .Select(entry => new { Entry = entry, Score = Score(entry, query, terms) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Entry.Confidence)
                .ThenByDescending(candidate => candidate.Entry.UpdatedUtc)
                .Take(limit)
                .Select(candidate => candidate.Entry)
                .ToArray();
        });
    }

    internal PcKnowledgeEntry Upsert(
        string key,
        string kind,
        string subject,
        string fact,
        string navigationHint,
        string source,
        int confidence)
    {
        key = ValidKey(key);
        kind = AllowedValue(kind, nameof(kind), AllowedKinds);
        subject = Bounded(subject, nameof(subject), MaxSubjectCharacters, allowEmpty: false);
        fact = Bounded(fact, nameof(fact), MaxFactCharacters, allowEmpty: false);
        navigationHint = Bounded(
            navigationHint,
            nameof(navigationHint),
            MaxNavigationHintCharacters,
            allowEmpty: true);
        source = AllowedValue(source, nameof(source), AllowedSources);
        if (confidence is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "confidence must be from 0 to 100.");
        }

        return WithStoreLock(() =>
        {
            var entries = Load();
            var index = entries.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (index < 0 && entries.Count >= MaxEntries)
            {
                throw new InvalidOperationException($"The PC knowledge store is limited to {MaxEntries} entries.");
            }

            var now = _timeProvider.GetUtcNow();
            var previous = index < 0 ? null : entries[index];
            var entry = new PcKnowledgeEntry(
                key,
                kind,
                subject,
                fact,
                navigationHint,
                source,
                confidence,
                previous?.CreatedUtc ?? now,
                now,
                (previous?.Revision ?? 0) + 1);
            if (index < 0)
            {
                entries.Add(entry);
            }
            else
            {
                entries[index] = entry;
            }

            Save(entries);
            return entry;
        });
    }

    internal bool Forget(string key)
    {
        key = ValidKey(key);
        return WithStoreLock(() =>
        {
            var entries = Load();
            var removed = entries.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save(entries);
            }

            return removed;
        });
    }

    private List<PcKnowledgeEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var file = new FileInfo(_path);
        if (file.Length > MaxStoreBytes)
        {
            throw new InvalidOperationException("The PC knowledge store exceeds its size limit.");
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = JsonSerializer.Deserialize<PcKnowledgeDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The PC knowledge store is empty or invalid.");
        if (document.Entries is null ||
            document.Version != CurrentVersion ||
            document.Entries.Count > MaxEntries)
        {
            throw new InvalidOperationException("The PC knowledge store version or entry count is invalid.");
        }

        var entries = document.Entries.ToList();
        if (entries.Any(entry => entry is null))
        {
            throw new InvalidOperationException("The PC knowledge store contains a null entry.");
        }

        if (entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new InvalidOperationException("The PC knowledge store contains duplicate keys.");
        }

        foreach (var entry in entries)
        {
            ValidateStoredEntry(entry);
        }

        return entries;
    }

    private void Save(IReadOnlyList<PcKnowledgeEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The PC knowledge store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new PcKnowledgeDocument(CurrentVersion, entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray()),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaxStoreBytes)
                {
                    throw new InvalidOperationException("The proposed PC knowledge update exceeds the store size limit.");
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

    private T WithStoreLock<T>(Func<T> operation)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(StoreLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new IOException("Timed out waiting for the PC knowledge store lock.");
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

    private static string MutexName(string path)
    {
        var normalized = path.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Local\\RapidPcUse.Knowledge.{Convert.ToHexString(digest)}";
    }

    private static void ValidateStoredEntry(PcKnowledgeEntry entry)
    {
        if (entry is null)
        {
            throw new InvalidOperationException("The PC knowledge store contains a null entry.");
        }

        _ = ValidKey(entry.Key);
        _ = AllowedValue(entry.Kind, nameof(entry.Kind), AllowedKinds);
        _ = Bounded(entry.Subject, nameof(entry.Subject), MaxSubjectCharacters, allowEmpty: false);
        _ = Bounded(entry.Fact, nameof(entry.Fact), MaxFactCharacters, allowEmpty: false);
        _ = Bounded(entry.NavigationHint, nameof(entry.NavigationHint), MaxNavigationHintCharacters, allowEmpty: true);
        _ = AllowedValue(entry.Source, nameof(entry.Source), AllowedSources);
        if (entry.Confidence is < 0 or > 100 || entry.Revision < 1 || entry.UpdatedUtc < entry.CreatedUtc)
        {
            throw new InvalidOperationException("The PC knowledge store contains invalid metadata.");
        }
    }

    private static int Score(PcKnowledgeEntry entry, string query, IReadOnlyList<string> terms)
    {
        var score = Contains(entry.Key, query) ? 24 : 0;
        score += Contains(entry.Subject, query) ? 18 : 0;
        score += Contains(entry.Fact, query) ? 10 : 0;
        score += Contains(entry.NavigationHint, query) ? 6 : 0;
        foreach (var term in terms)
        {
            score += Contains(entry.Key, term) ? 6 : 0;
            score += Contains(entry.Subject, term) ? 5 : 0;
            score += Contains(entry.Fact, term) ? 3 : 0;
            score += Contains(entry.NavigationHint, term) ? 2 : 0;
        }

        return score;
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string ValidKey(string value)
    {
        value = Bounded(value, nameof(value), MaxKeyCharacters, allowEmpty: false).ToLowerInvariant();
        if (!char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("key must use lowercase ASCII letters, digits, '.', '_', or '-'.");
        }

        return value;
    }

    private static string AllowedValue(string value, string name, HashSet<string> allowed)
    {
        value = Bounded(value, name, 32, allowEmpty: false).ToLowerInvariant();
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{name} is not recognized.");
        }

        return value;
    }

    private static string Bounded(string value, string name, int maximum, bool allowEmpty)
    {
        if (value is null)
        {
            throw new ArgumentException($"{name} is missing.");
        }

        value = value.Trim();
        if ((!allowEmpty && value.Length == 0) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must contain {(allowEmpty ? "0" : "1")} to {maximum} non-control characters.");
        }

        return value;
    }
}
