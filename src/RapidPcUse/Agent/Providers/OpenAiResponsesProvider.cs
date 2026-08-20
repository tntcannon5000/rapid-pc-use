using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RapidPcUse.Agent.Providers;

internal sealed class OpenAiResponsesProvider : IPcModelProvider
{
    private static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");
    private readonly string _apiKey;
    private readonly PcAgentOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    internal OpenAiResponsesProvider(string apiKey, PcAgentOptions options, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for the OpenAI PC agent provider.");
        }

        _apiKey = apiKey;
        _options = options;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = httpClient is null;
    }

    public string Name => "openai";

    public string Model => _options.Model;

    public async Task<PcModelTurnResult> DecideAsync(
        PcModelTurnRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = BuildRequest(request);
        var requestBuilt = Stopwatch.GetTimestamp();
        using var message = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint)
        {
            Content = new ByteArrayContent(payload),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var headersReceived = Stopwatch.GetTimestamp();
        if (!response.IsSuccessStatusCode)
        {
            await DrainBoundedErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new PcAgentProviderException(response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8_192,
            leaveOpen: false);

        string? functionName = null;
        string? functionArguments = null;
        ProviderUsage usage = new(null, null, null, null);
        long? firstEventTimestamp = null;
        long? firstDecisionDeltaTimestamp = null;
        long? decisionCompleteTimestamp = null;
        var totalCharacters = 0;
        var sawCompleted = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            totalCharacters = checked(totalCharacters + Encoding.UTF8.GetByteCount(line) + 1);
            if (totalCharacters > SecurityLimits.MaxAgentProviderResponseBytes)
            {
                throw new InvalidOperationException("The provider response exceeded its configured size limit.");
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line.AsSpan(5).TrimStart();
            if (data.SequenceEqual("[DONE]"))
            {
                break;
            }

            if (data.Length == 0 || data.Length > SecurityLimits.MaxAgentProviderEventBytes)
            {
                if (data.Length > SecurityLimits.MaxAgentProviderEventBytes)
                {
                    throw new InvalidOperationException("A provider stream event exceeded its configured size limit.");
                }

                continue;
            }

            firstEventTimestamp ??= Stopwatch.GetTimestamp();
            using var document = JsonDocument.Parse(data.ToString(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = document.RootElement;
            var eventType = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            switch (eventType)
            {
                case "response.function_call_arguments.delta":
                    firstDecisionDeltaTimestamp ??= Stopwatch.GetTimestamp();
                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item) && TryReadFunctionCall(item, out var name, out var arguments))
                    {
                        if (functionName is not null)
                        {
                            throw new InvalidOperationException("The provider returned more than one decision tool call.");
                        }

                        functionName = name;
                        functionArguments = arguments;
                        decisionCompleteTimestamp = Stopwatch.GetTimestamp();
                    }

                    break;
                case "response.completed":
                    sawCompleted = true;
                    if (root.TryGetProperty("response", out var completedResponse))
                    {
                        usage = ReadUsage(completedResponse);
                        if (functionName is null && completedResponse.TryGetProperty("output", out var output))
                        {
                            foreach (var outputItem in output.EnumerateArray())
                            {
                                if (!TryReadFunctionCall(outputItem, out var outputName, out var outputArguments))
                                {
                                    continue;
                                }

                                if (functionName is not null)
                                {
                                    throw new InvalidOperationException("The provider returned more than one decision tool call.");
                                }

                                functionName = outputName;
                                functionArguments = outputArguments;
                                decisionCompleteTimestamp = Stopwatch.GetTimestamp();
                            }
                        }
                    }

                    break;
                case "response.failed":
                case "error":
                    throw new InvalidOperationException("The provider reported a failed response.");
            }

            if (sawCompleted)
            {
                break;
            }
        }

        if (!sawCompleted || functionName is null || functionArguments is null)
        {
            throw new InvalidOperationException("The provider did not return one completed decision tool call.");
        }

        var completed = Stopwatch.GetTimestamp();
        var parseStarted = Stopwatch.GetTimestamp();
        var decision = PcAgentDecisionParser.Parse(functionName, functionArguments);
        var parseCompleted = Stopwatch.GetTimestamp();
        return new PcModelTurnResult(
            decision,
            Name,
            Model,
            payload.Length,
            request.Observation.Frames.Count,
            request.Observation.Frames.Sum(frame => frame.Bytes.Length),
            ElapsedMicroseconds(parseStarted, parseCompleted),
            new ProviderLocalStageTimings(0, 0, 0, ElapsedMicroseconds(started, requestBuilt)),
            new ProviderTurnTimings(
                ElapsedMicroseconds(started, requestBuilt),
                ElapsedMicroseconds(started, headersReceived),
                ElapsedMicroseconds(started, firstEventTimestamp ?? completed),
                ElapsedMicroseconds(started, firstDecisionDeltaTimestamp ?? decisionCompleteTimestamp ?? completed),
                ElapsedMicroseconds(started, decisionCompleteTimestamp ?? completed)),
            usage);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    internal byte[] BuildRequest(PcModelTurnRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _options.Model);
            writer.WriteString("service_tier", _options.ServiceTier);
            writer.WriteString("instructions", PcAgentPrompt.Instructions);
            writer.WriteBoolean("store", false);
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("parallel_tool_calls", false);
            writer.WriteNumber("max_output_tokens", SecurityLimits.MaxAgentOutputTokens);
            writer.WriteString("tool_choice", "required");
            writer.WriteString("prompt_cache_key", $"rapid-pc-use-{PcAgentPrompt.Version}-{_options.Model}-{_options.ReasoningEffort}");
            if (_options.ReasoningEffort != "none")
            {
                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", _options.ReasoningEffort);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("tools");
            OpenAiDecisionSchema.WriteTools(writer);
            writer.WritePropertyName("input");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "input_text");
            writer.WriteString("text", PcAgentPrompt.BuildTurnText(request));
            writer.WriteEndObject();
            foreach (var frame in request.Observation.Frames)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "input_image");
                WriteImageDataUrl(writer, frame);
                writer.WriteString("detail", _options.ImageDetail);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteImageDataUrl(Utf8JsonWriter writer, ScreenFrame frame)
    {
        var prefix = $"data:{frame.MimeType};base64,";
        var base64Length = checked(4 * ((frame.Bytes.Length + 2) / 3));
        var totalLength = checked(prefix.Length + base64Length);
        var rented = ArrayPool<char>.Shared.Rent(totalLength);
        try
        {
            prefix.AsSpan().CopyTo(rented);
            if (!Convert.TryToBase64Chars(
                    frame.Bytes,
                    rented.AsSpan(prefix.Length, base64Length),
                    out var charactersWritten) ||
                charactersWritten != base64Length)
            {
                throw new InvalidOperationException("The current screenshot could not be encoded for the provider request.");
            }

            writer.WriteString("image_url", rented.AsSpan(0, totalLength));
        }
        finally
        {
            Array.Clear(rented, 0, totalLength);
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool TryReadFunctionCall(JsonElement item, out string name, out string arguments)
    {
        name = "";
        arguments = "";
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("type", out var type) || type.GetString() != "function_call" ||
            !item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("arguments", out var argumentsElement) || argumentsElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        name = nameElement.GetString()!;
        arguments = argumentsElement.GetString()!;
        if (name.Length > 128 || arguments.Length > SecurityLimits.MaxAgentProviderArgumentsCharacters)
        {
            throw new InvalidOperationException("The provider decision exceeded its configured field limits.");
        }

        return true;
    }

    private static ProviderUsage ReadUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new ProviderUsage(null, null, null, null);
        }

        return new ProviderUsage(
            OptionalInt64(usage, "input_tokens"),
            NestedOptionalInt64(usage, "input_tokens_details", "cached_tokens"),
            OptionalInt64(usage, "output_tokens"),
            NestedOptionalInt64(usage, "output_tokens_details", "reasoning_tokens"));
    }

    private static long? OptionalInt64(JsonElement value, string property)
        => value.TryGetProperty(property, out var element) && element.TryGetInt64(out var result) && result >= 0
            ? result
            : null;

    private static long? NestedOptionalInt64(JsonElement value, string parent, string property)
        => value.TryGetProperty(parent, out var parentElement) && parentElement.ValueKind == JsonValueKind.Object
            ? OptionalInt64(parentElement, property)
            : null;

    private static async Task DrainBoundedErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Min(SecurityLimits.MaxAgentProviderErrorBytes, 4_096)];
        var total = 0;
        while (total < SecurityLimits.MaxAgentProviderErrorBytes)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, SecurityLimits.MaxAgentProviderErrorBytes - total)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }
    }

    private static long ElapsedMicroseconds(long start, long end)
        => (long)(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds * 1000);
}

internal sealed class PcAgentProviderException(HttpStatusCode statusCode)
    : Exception("The configured PC agent provider rejected the request.")
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
}
