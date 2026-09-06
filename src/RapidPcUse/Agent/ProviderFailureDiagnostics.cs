using System.IO;
using System.Net.Http;
using System.Text.Json;
using RapidPcUse.Agent.Providers;

namespace RapidPcUse.Agent;

internal sealed class PcModelProviderStageException(
    string stage,
    string reasonCode,
    Exception innerException)
    : InvalidOperationException("The PC model provider failed during a bounded stage.", innerException)
{
    internal string Stage { get; } = stage;

    internal string ReasonCode { get; } = reasonCode;

    internal static PcModelProviderStageException Wrap(string stage, Exception exception)
        => new(stage, ProviderFailureDiagnostics.ReasonCode(exception), exception);
}

internal sealed record ProviderFailureSnapshot(
    string Stage,
    string ReasonCode,
    string ExceptionType,
    string HResult);

internal static class ProviderFailureDiagnostics
{
    internal static ProviderFailureSnapshot Capture(Exception exception)
    {
        var staged = exception as PcModelProviderStageException;
        var root = staged?.InnerException ?? exception;
        return new ProviderFailureSnapshot(
            staged?.Stage ?? "decision",
            staged?.ReasonCode ?? ReasonCode(root),
            root.GetType().FullName ?? root.GetType().Name,
            $"0x{root.HResult:X8}");
    }

    internal static string ReasonCode(Exception exception)
        => exception switch
        {
            CodexAppServerException codex => codex.ReasonCode,
            PcAgentProviderException provider => $"http_{(int)provider.StatusCode}",
            HttpRequestException => "http_transport",
            IOException => "stream_io",
            JsonException => "protocol_json",
            InvalidOperationException => "invalid_operation",
            _ => "unexpected_exception",
        };
}
