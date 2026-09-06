using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RapidPcUse.Agent.Providers;

/// <summary>
/// Runs the visual decision loop through Codex's supported app-server protocol.
/// Codex retains ownership of ChatGPT authentication; this provider never reads,
/// copies, or receives OAuth credentials.
/// </summary>
internal sealed class CodexAppServerProvider : IPcModelProvider, IWarmablePcModelProvider
{
    private const int RecycleAfterTurns = 96;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private readonly PcAgentOptions _options;
    private readonly string _codexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CodexConnection? _connection;
    private string? _preparedThreadId;
    private int _successfulTurns;
    private bool _disposed;

    internal CodexAppServerProvider(PcAgentOptions options, string? codexPath = null)
    {
        _options = options;
        _codexPath = codexPath ?? CodexExecutableLocator.Find();
    }

    public string Name => "codex-session";

    public string Model => _options.Model;

    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (_preparedThreadId is null)
            {
                _preparedThreadId = await StartThreadAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PcModelTurnResult> DecideAsync(
        PcModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_successfulTurns >= RecycleAfterTurns)
            {
                ResetConnection();
            }

            var started = Stopwatch.GetTimestamp();
            var stage = "image_stage";
            var frameDirectory = CreateFrameDirectory();
            try
            {
                var imagePaths = await StageCurrentFramesAsync(
                    frameDirectory,
                    request.Observation.Frames,
                    cancellationToken).ConfigureAwait(false);
                var framesStaged = Stopwatch.GetTimestamp();
                stage = "connection_acquire";
                var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connectionReady = Stopwatch.GetTimestamp();
                var threadId = _preparedThreadId;
                _preparedThreadId = null;
                if (threadId is null)
                {
                    stage = "thread_start";
                    threadId = await StartThreadAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                var threadReady = Stopwatch.GetTimestamp();
                stage = "payload_build";
                var turnRequest = BuildTurnStartRequest(connection.NextId(), threadId, request, imagePaths);
                var requestBuilt = Stopwatch.GetTimestamp();
                stage = "turn_wait";
                var result = await connection.RunTurnAsync(
                    turnRequest,
                    threadId,
                    started,
                    requestBuilt,
                    cancellationToken).ConfigureAwait(false);
                _successfulTurns++;

                stage = "decision_parse";
                var parseStarted = Stopwatch.GetTimestamp();
                var decision = PcAgentDecisionParser.ParseStructured(result.Output);
                var parseCompleted = Stopwatch.GetTimestamp();
                return new PcModelTurnResult(
                    decision,
                    Name,
                    Model,
                    turnRequest.Length,
                    request.Observation.Frames.Count,
                    request.Observation.Frames.Sum(frame => frame.Bytes.Length),
                    (long)(Stopwatch.GetElapsedTime(parseStarted, parseCompleted).TotalMilliseconds * 1_000),
                    new ProviderLocalStageTimings(
                        ElapsedMicroseconds(started, framesStaged),
                        ElapsedMicroseconds(framesStaged, connectionReady),
                        ElapsedMicroseconds(connectionReady, threadReady),
                        ElapsedMicroseconds(threadReady, requestBuilt)),
                    result.Timings,
                    result.Usage);
            }
            catch (OperationCanceledException)
            {
                ResetConnection();
                throw;
            }
            catch (Exception exception)
            {
                ResetConnection();
                throw PcModelProviderStageException.Wrap(stage, exception);
            }
            finally
            {
                DeleteFrameDirectory(frameDirectory);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();
        _gate.Dispose();
    }

    internal byte[] BuildThreadStartRequest(long id)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method", "thread/start");
            writer.WriteNumber("id", id);
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteString("baseInstructions", PcAgentPrompt.StructuredInstructions);
            writer.WriteString(
                "developerInstructions",
                "Use only the supplied text and current screenshots. Return the schema response immediately without invoking tools.");
            writer.WriteString("serviceTier", _options.ServiceTier);
            writer.WriteBoolean("ephemeral", true);
            writer.WriteString("approvalPolicy", "never");
            writer.WriteString("sandbox", "read-only");
            writer.WriteString("cwd", Path.GetTempPath());
            writer.WriteString("serviceName", "rapid_pc_use");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    internal byte[] BuildTurnStartRequest(
        long id,
        string threadId,
        PcModelTurnRequest request,
        IReadOnlyList<string> imagePaths)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method", "turn/start");
            writer.WriteNumber("id", id);
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteString("threadId", threadId);
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", PcAgentPrompt.BuildTurnText(request));
            writer.WriteEndObject();
            foreach (var path in imagePaths)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "localImage");
                writer.WriteString("path", path);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("model", _options.Model);
            writer.WriteString("effort", _options.ReasoningEffort);
            writer.WriteString("serviceTier", _options.ServiceTier);
            writer.WriteString("approvalPolicy", "never");
            writer.WritePropertyName("sandboxPolicy");
            writer.WriteStartObject();
            writer.WriteString("type", "readOnly");
            writer.WriteEndObject();
            writer.WritePropertyName("outputSchema");
            OpenAiDecisionSchema.WriteStructuredOutput(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task<CodexConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = new CodexConnection(_codexPath, _options.ServiceTier);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(StartupTimeout);
            await connection.InitializeAsync(deadline.Token).ConfigureAwait(false);
            _connection = connection;
            _successfulTurns = 0;
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ResetConnection()
    {
        _connection?.Dispose();
        _connection = null;
        _preparedThreadId = null;
        _successfulTurns = 0;
    }

    private async Task<string> StartThreadAsync(
        CodexConnection connection,
        CancellationToken cancellationToken)
    {
        var request = BuildThreadStartRequest(connection.NextId());
        var result = await connection.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return ReadThreadId(result);
    }

    private static string ReadThreadId(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new CodexAppServerException("thread_create_invalid", "Codex app-server did not create an ephemeral decision thread.");
        }

        return id.GetString()!;
    }

    private static long ElapsedMicroseconds(long started, long completed)
        => (long)(Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds * 1_000);

    private static string CreateFrameDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rapid-pc-use-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<IReadOnlyList<string>> StageCurrentFramesAsync(
        string directory,
        IReadOnlyList<ScreenFrame> frames,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>(frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            var extension = frames[index].MimeType == "image/png" ? ".png" : ".jpg";
            var path = Path.Combine(directory, $"display-{index + 1}{extension}");
            await File.WriteAllBytesAsync(path, frames[index].Bytes, cancellationToken).ConfigureAwait(false);
            paths.Add(path);
        }

        return paths;
    }

    private static void DeleteFrameDirectory(string path)
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("rapid-pc-use-frames-", StringComparison.Ordinal))
        {
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt == 0)
            {
                // A scanner may briefly retain a frame handle after app-server completes.
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException) when (attempt == 0)
            {
                Thread.Sleep(10);
            }
            catch (IOException)
            {
                DriverLog.Warning("agent.frame_cleanup_failed", "A temporary agent frame could not be deleted immediately.");
            }
            catch (UnauthorizedAccessException)
            {
                DriverLog.Warning("agent.frame_cleanup_failed", "A temporary agent frame could not be deleted immediately.");
            }
        }
    }

    private sealed record CodexTurnResult(
        string Output,
        ProviderTurnTimings Timings,
        ProviderUsage Usage);

    private sealed class CodexConnection : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private long _nextId;
        private bool _disposed;

        internal CodexConnection(string codexPath, string serviceTier)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true),
            };
            AddConfig(startInfo, "service_tier", serviceTier);
            AddConfig(startInfo, "forced_login_method", "chatgpt");
            AddConfig(startInfo, "features.shell_tool", "false");
            AddConfig(startInfo, "features.plugins", "false");
            AddConfig(startInfo, "memories.generate_memories", "false");
            AddConfig(startInfo, "web_search", "disabled");
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");

            _process = new Process { StartInfo = startInfo };
            if (!_process.Start())
            {
                throw new CodexAppServerException("process_start_failed", "Codex app-server could not be started.");
            }

            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _output = _process.StandardOutput;
            _process.BeginErrorReadLine();
        }

        internal long NextId() => Interlocked.Increment(ref _nextId);

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            var initialize = BuildSimpleRequest(
                NextId(),
                "initialize",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("clientInfo");
                    writer.WriteStartObject();
                    writer.WriteString("name", "rapid_pc_use");
                    writer.WriteString("title", "Rapid PC Use");
                    writer.WriteString("version", BuildInfo.Version);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                });
            _ = await RequestAsync(initialize, cancellationToken).ConfigureAwait(false);
            await WriteAsync(Encoding.UTF8.GetBytes("{\"method\":\"initialized\",\"params\":{}}"), cancellationToken)
                .ConfigureAwait(false);

            var accountRequest = BuildSimpleRequest(
                NextId(),
                "account/read",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("refreshToken", false);
                    writer.WriteEndObject();
                });
            var accountResult = await RequestAsync(accountRequest, cancellationToken).ConfigureAwait(false);
            if (!accountResult.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
            {
                throw new CodexAppServerException("chatgpt_sign_in_missing", "Codex is not signed in with ChatGPT.");
            }

            if (account.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "apiKey", StringComparison.OrdinalIgnoreCase))
            {
                throw new CodexAppServerException("chatgpt_auth_required", "The PC agent requires Codex ChatGPT-session authentication, not an API key.");
            }
        }

        internal async Task<JsonElement> RequestAsync(byte[] request, CancellationToken cancellationToken)
        {
            using var requestDocument = JsonDocument.Parse(request);
            var expectedId = requestDocument.RootElement.GetProperty("id").GetInt64();
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var message = await ReadAsync(cancellationToken).ConfigureAwait(false);
                var root = message.RootElement;
                if (TryReadResponseId(root, out var responseId) && responseId == expectedId)
                {
                    return ReadResult(root);
                }

                if (TryReadServerRequest(root, out var serverRequestId))
                {
                    await RejectServerRequestAsync(serverRequestId, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        internal async Task<CodexTurnResult> RunTurnAsync(
            byte[] request,
            string threadId,
            long started,
            long requestBuilt,
            CancellationToken cancellationToken)
        {
            using var requestDocument = JsonDocument.Parse(request);
            var expectedId = requestDocument.RootElement.GetProperty("id").GetInt64();
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);

            string? turnId = null;
            string? output = null;
            ProviderUsage usage = new(null, null, null, null);
            long? responseTimestamp = null;
            long? firstEventTimestamp = null;
            long? firstDeltaTimestamp = null;
            long? decisionTimestamp = null;
            while (true)
            {
                using var message = await ReadAsync(cancellationToken).ConfigureAwait(false);
                var root = message.RootElement;
                if (TryReadResponseId(root, out var responseId) && responseId == expectedId)
                {
                    responseTimestamp = Stopwatch.GetTimestamp();
                    var result = ReadResult(root);
                    if (!result.TryGetProperty("turn", out var turn) ||
                        !turn.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String)
                    {
                        throw new CodexAppServerException("turn_start_invalid", "Codex app-server did not start the decision turn.");
                    }

                    turnId = id.GetString();
                    continue;
                }

                if (TryReadServerRequest(root, out var serverRequestId))
                {
                    await RejectServerRequestAsync(serverRequestId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var method = methodElement.GetString();
                if (!MatchesThread(parameters, threadId))
                {
                    continue;
                }

                firstEventTimestamp ??= Stopwatch.GetTimestamp();
                switch (method)
                {
                    case "item/agentMessage/delta":
                        firstDeltaTimestamp ??= Stopwatch.GetTimestamp();
                        break;
                    case "item/completed":
                        if (parameters.TryGetProperty("item", out var item) &&
                            item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agentMessage" &&
                            item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            output = text.GetString();
                            decisionTimestamp = Stopwatch.GetTimestamp();
                        }

                        break;
                    case "thread/tokenUsage/updated":
                        usage = ReadUsage(parameters);
                        break;
                    case "turn/completed":
                        if (turnId is not null && !MatchesTurn(parameters, turnId))
                        {
                            continue;
                        }

                        EnsureCompleted(parameters);
                        if (string.IsNullOrWhiteSpace(output))
                        {
                            throw new CodexAppServerException("decision_missing", "Codex app-server completed without a structured decision.");
                        }

                        var completed = Stopwatch.GetTimestamp();
                        return new CodexTurnResult(
                            output,
                            new ProviderTurnTimings(
                                ElapsedMicroseconds(started, requestBuilt),
                                ElapsedMicroseconds(started, responseTimestamp ?? completed),
                                ElapsedMicroseconds(started, firstEventTimestamp ?? completed),
                                ElapsedMicroseconds(started, firstDeltaTimestamp ?? decisionTimestamp ?? completed),
                                ElapsedMicroseconds(started, decisionTimestamp ?? completed)),
                            usage);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _input.Dispose();
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2_000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _output.Dispose();
                _process.Dispose();
            }
        }

        private static void AddConfig(ProcessStartInfo startInfo, string name, string value)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"{name}={value}");
        }

        private async Task WriteAsync(byte[] message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Length > SecurityLimits.MaxAgentProviderResponseBytes)
            {
                throw new CodexAppServerException("request_oversized", "The Codex app-server request exceeded its configured size limit.");
            }

            await _input.WriteLineAsync(Encoding.UTF8.GetString(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken)
        {
            var line = await _output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new CodexAppServerException("response_stream_closed", "Codex app-server closed its response stream.");
            }

            if (Encoding.UTF8.GetByteCount(line) > SecurityLimits.MaxAgentProviderResponseBytes)
            {
                throw new CodexAppServerException("response_oversized", "A Codex app-server message exceeded its configured size limit.");
            }

            return JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }

        private async Task RejectServerRequestAsync(long id, CancellationToken cancellationToken)
        {
            var response = Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"error\":{{\"code\":-32601,\"message\":\"Tools are disabled for this visual decision route.\"}}}}");
            await WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }

        private static JsonElement ReadResult(JsonElement response)
        {
            if (response.TryGetProperty("error", out _))
            {
                throw new CodexAppServerException("protocol_request_rejected", "Codex app-server rejected a protocol request.");
            }

            if (!response.TryGetProperty("result", out var result))
            {
                throw new CodexAppServerException("protocol_response_invalid", "Codex app-server returned an invalid protocol response.");
            }

            return result.Clone();
        }

        private static bool TryReadResponseId(JsonElement root, out long id)
        {
            id = 0;
            return root.TryGetProperty("id", out var element) && element.TryGetInt64(out id) &&
                !root.TryGetProperty("method", out _);
        }

        private static bool TryReadServerRequest(JsonElement root, out long id)
        {
            id = 0;
            return root.TryGetProperty("id", out var element) && element.TryGetInt64(out id) &&
                root.TryGetProperty("method", out _);
        }

        private static bool MatchesThread(JsonElement parameters, string threadId)
            => parameters.TryGetProperty("threadId", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), threadId, StringComparison.Ordinal);

        private static bool MatchesTurn(JsonElement parameters, string turnId)
            => parameters.TryGetProperty("turn", out var turn) &&
                turn.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), turnId, StringComparison.Ordinal);

        private static void EnsureCompleted(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("turn", out var turn) ||
                !turn.TryGetProperty("status", out var status))
            {
                throw new CodexAppServerException("turn_completion_invalid", "Codex app-server returned an invalid turn completion.");
            }

            if (status.GetString() != "completed")
            {
                var detail = "";
                if (turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    detail = message.GetString()!.Replace('\r', ' ').Replace('\n', ' ');
                    detail = detail[..Math.Min(detail.Length, 300)];
                }

                throw new CodexAppServerException(
                    "turn_not_completed",
                    $"Codex app-server decision turn ended as '{status.GetString()}'. {detail}".TrimEnd());
            }
        }

        private static ProviderUsage ReadUsage(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("tokenUsage", out var tokenUsage) ||
                !tokenUsage.TryGetProperty("last", out var last))
            {
                return new ProviderUsage(null, null, null, null);
            }

            return new ProviderUsage(
                OptionalInt64(last, "inputTokens"),
                OptionalInt64(last, "cachedInputTokens"),
                OptionalInt64(last, "outputTokens"),
                OptionalInt64(last, "reasoningOutputTokens"));
        }

        private static long? OptionalInt64(JsonElement value, string property)
            => value.TryGetProperty(property, out var element) && element.TryGetInt64(out var result) && result >= 0
                ? result
                : null;

        private static byte[] BuildSimpleRequest(long id, string method, Action<Utf8JsonWriter> writeParameters)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("method", method);
                writer.WriteNumber("id", id);
                writer.WritePropertyName("params");
                writeParameters(writer);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        private static long ElapsedMicroseconds(long start, long end)
            => (long)(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds * 1000);
    }
}

internal sealed class CodexAppServerException(string reasonCode, string message)
    : InvalidOperationException(message)
{
    internal string ReasonCode { get; } = reasonCode;
}

internal static class CodexExecutableLocator
{
    internal static string Find()
    {
        var configured = Environment.GetEnvironmentVariable("RAPID_PC_AGENT_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Validate(configured);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var desktopBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktopBin))
        {
            // Desktop updates retain older launchers beside versioned runtimes.
            // The newest binary is required for newly introduced Codex models.
            var desktopExecutable = Directory
                .EnumerateFiles(desktopBin, "codex.exe", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (desktopExecutable is not null)
            {
                return desktopExecutable.FullName;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "codex.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException("A Codex CLI executable is required for the Codex-session PC agent provider.");
    }

    private static string Validate(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetFileName(fullPath), "codex.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new InvalidOperationException("RAPID_PC_AGENT_CODEX_PATH must name an existing Codex executable.");
        }

        return fullPath;
    }
}
