using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RapidPcUse.Agent.Providers;

internal sealed class BrokeredModelProvider : IPcModelProvider
{
    private const int ProtocolVersion = 1;
    private static readonly Regex SafePipeName = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    private readonly PcAgentOptions _options;
    private readonly string _pipeName;
    private readonly string _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    internal BrokeredModelProvider(PcAgentOptions options)
        : this(
            options,
            Environment.GetEnvironmentVariable("RAPID_PC_AGENT_BROKER_PIPE") ?? "",
            Environment.GetEnvironmentVariable("RAPID_PC_AGENT_BROKER_TOKEN") ?? "")
    {
    }

    internal BrokeredModelProvider(PcAgentOptions options, string pipeName, string token)
    {
        _options = options;
        _pipeName = ValidatePipeName(pipeName);
        _token = ValidateToken(token);
    }

    public string Name => "broker";

    public string Model => _options.Model;

    public async Task<PcModelTurnResult> DecideAsync(
        PcModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var requestId = Guid.NewGuid().ToString("N");
            var payload = BuildRequest(requestId, request);
            var payloadBytes = Encoding.UTF8.GetByteCount(payload) + 1;
            if (payloadBytes > SecurityLimits.MaxAgentBrokerRequestBytes)
            {
                throw new InvalidOperationException("The broker request exceeded its configured byte limit.");
            }

            var requestBuilt = Stopwatch.GetTimestamp();
            var connectionAcquireStarted = Stopwatch.GetTimestamp();
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var connectionAcquired = Stopwatch.GetTimestamp();

            try
            {
                await _writer!.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                var responseLine = await ReadBoundedLineAsync(_reader!, cancellationToken).ConfigureAwait(false);
                var completed = Stopwatch.GetTimestamp();
                var response = ParseResponse(responseLine, requestId);

                var parseStarted = Stopwatch.GetTimestamp();
                var decision = PcAgentDecisionParser.ParseStructured(response.Arguments!);
                var parseCompleted = Stopwatch.GetTimestamp();
                var requestBuildUs = ElapsedMicroseconds(started, requestBuilt);
                var connectionAcquireUs = ElapsedMicroseconds(connectionAcquireStarted, connectionAcquired);
                return new PcModelTurnResult(
                    decision,
                    response.Provider!,
                    response.Model!,
                    payloadBytes,
                    request.Observation.Frames.Count,
                    request.Observation.Frames.Sum(frame => frame.Bytes.Length),
                    ElapsedMicroseconds(parseStarted, parseCompleted),
                    new ProviderLocalStageTimings(
                        response.Timings!.AttachmentMicroseconds,
                        connectionAcquireUs,
                        response.Timings.PrepareMicroseconds,
                        requestBuildUs),
                    new ProviderTurnTimings(
                        requestBuildUs,
                        0,
                        requestBuildUs + connectionAcquireUs + response.Timings.FirstEventMicroseconds,
                        requestBuildUs + connectionAcquireUs + response.Timings.FirstDecisionMicroseconds,
                        Math.Max(
                            requestBuildUs + connectionAcquireUs + response.Timings.DecisionCompleteMicroseconds,
                            ElapsedMicroseconds(started, completed))),
                    new ProviderUsage(
                        response.Usage?.InputTokens,
                        response.Usage?.CachedInputTokens,
                        response.Usage?.OutputTokens,
                        response.Usage?.ReasoningTokens));
            }
            catch
            {
                ResetConnection();
                throw;
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

    private string BuildRequest(string requestId, PcModelTurnRequest request)
    {
        var brokerRequest = new BrokerRequest(
            ProtocolVersion,
            _token,
            requestId,
            request.RunId,
            PcAgentPrompt.StructuredInstructions,
            PcAgentPrompt.BuildTurnText(request),
            "computer_decide",
            "Return exactly one validated Rapid PC Use decision for the current screenshot and bounded state.",
            OpenAiDecisionSchema.StructuredOutputElement,
            request.Observation.Frames.Select((frame, index) => new BrokerImage(
                frame.MimeType,
                Convert.ToBase64String(frame.Bytes),
                $"rapid-pc-use-frame-{index + 1}.{MediaExtension(frame.MimeType)}")).ToArray());
        return JsonSerializer.Serialize(brokerRequest, JsonOptions);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        ResetConnection();
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8_192,
            leaveOpen: true);
        _writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false, true),
            bufferSize: 8_192,
            leaveOpen: true)
        {
            NewLine = "\n",
        };
    }

    private static BrokerResponse ParseResponse(string responseLine, string requestId)
    {
        BrokerResponse response;
        try
        {
            response = JsonSerializer.Deserialize<BrokerResponse>(responseLine, JsonOptions) ??
                throw new InvalidOperationException("The model broker returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The model broker returned malformed JSON.", exception);
        }

        if (response.ProtocolVersion != ProtocolVersion ||
            !FixedTimeEquals(response.RequestId, requestId))
        {
            throw new InvalidOperationException("The model broker response identity is invalid.");
        }

        if (!response.Ok)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.ErrorCode)
                    ? "The DSH model broker rejected the decision request."
                    : $"The DSH model broker rejected the decision request ({response.ErrorCode}).");
        }

        if (response.FunctionName != "computer_decide" ||
            string.IsNullOrWhiteSpace(response.Arguments) ||
            string.IsNullOrWhiteSpace(response.Provider) ||
            string.IsNullOrWhiteSpace(response.Model) ||
            response.Timings is null)
        {
            throw new InvalidOperationException("The DSH model broker response is incomplete.");
        }

        response.Timings.Validate();
        return response;
    }

    private static async Task<string> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(8_192, SecurityLimits.MaxAgentBrokerResponseCharacters));
        var buffer = new char[4_096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The DSH model broker closed before returning a decision.");
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return result.ToString().TrimEnd('\r');
                }

                if (result.Length >= SecurityLimits.MaxAgentBrokerResponseCharacters)
                {
                    throw new InvalidOperationException("The DSH model broker response exceeded its configured limit.");
                }

                result.Append(character);
            }
        }
    }

    private void ResetConnection()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private static string ValidatePipeName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > SecurityLimits.MaxAgentBrokerPipeNameCharacters || !SafePipeName.IsMatch(trimmed))
        {
            throw new InvalidOperationException("RAPID_PC_AGENT_BROKER_PIPE is unavailable or invalid.");
        }

        return trimmed;
    }

    private static string ValidateToken(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 32 or > SecurityLimits.MaxAgentBrokerTokenCharacters ||
            trimmed.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("RAPID_PC_AGENT_BROKER_TOKEN is unavailable or invalid.");
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private static string MediaExtension(string mediaType) => mediaType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        _ => "bin",
    };

    private static long ElapsedMicroseconds(long start, long end)
        => (long)(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds * 1_000);

    private sealed record BrokerRequest(
        int ProtocolVersion,
        string Token,
        string RequestId,
        string RunId,
        string System,
        string Prompt,
        string ToolName,
        string ToolDescription,
        JsonElement DecisionSchema,
        IReadOnlyList<BrokerImage> Images);

    private sealed record BrokerImage(string MediaType, string Data, string Name);

    private sealed record BrokerResponse(
        int ProtocolVersion,
        string? RequestId,
        bool Ok,
        string? FunctionName,
        string? Arguments,
        string? Provider,
        string? Model,
        BrokerTimings? Timings,
        BrokerUsage? Usage,
        string? ErrorCode);

    private sealed record BrokerTimings(
        long AttachmentMicroseconds,
        long PrepareMicroseconds,
        long FirstEventMicroseconds,
        long FirstDecisionMicroseconds,
        long DecisionCompleteMicroseconds)
    {
        internal void Validate()
        {
            if (AttachmentMicroseconds < 0 ||
                PrepareMicroseconds < 0 ||
                FirstEventMicroseconds < 0 ||
                FirstDecisionMicroseconds < FirstEventMicroseconds ||
                DecisionCompleteMicroseconds < FirstDecisionMicroseconds)
            {
                throw new InvalidOperationException("The DSH model broker returned invalid timing data.");
            }
        }
    }

    private sealed record BrokerUsage(
        long? InputTokens,
        long? CachedInputTokens,
        long? OutputTokens,
        long? ReasoningTokens);
}
