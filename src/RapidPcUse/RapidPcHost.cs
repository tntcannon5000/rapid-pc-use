using RapidPcUse.Agent;
using RapidPcUse.Agent.Providers;

namespace RapidPcUse;

internal sealed class RapidPcHost : IDisposable
{
    private readonly DesktopController _desktop = new();
    private readonly PcAgentLoop? _agent;
    private readonly CancellationTokenSource? _providerWarmupCancellation;
    private readonly Task? _providerWarmup;
    private readonly McpServer _mcp;

    internal RapidPcHost()
    {
        try
        {
            var options = PcAgentOptions.FromEnvironment();
            if (options.Enabled)
            {
                var provider = ProviderFactory.Create(options);
                _agent = new PcAgentLoop(_desktop, provider, options);
                if (provider is IWarmablePcModelProvider warmable)
                {
                    _providerWarmupCancellation = new CancellationTokenSource();
                    _providerWarmup = Task.Run(
                        () => WarmProviderAsync(warmable, _providerWarmupCancellation.Token));
                }

                DriverLog.Info(
                    "agent.enabled",
                    "The internal PC agent loop is enabled.",
                    data: new { provider = options.Provider, model = options.Model });
            }
        }
        catch (Exception exception)
        {
            DriverLog.Warning(
                "agent.disabled",
                "The internal PC agent loop is disabled because its startup configuration is unavailable or invalid; low-level PC control remains available.",
                exception: exception);
        }

        _mcp = new McpServer(_desktop, _agent, Console.In, Console.Out);
    }

    internal void Run() => _mcp.Run();

    public void Dispose()
    {
        _providerWarmupCancellation?.Cancel();
        if (_providerWarmup is not null)
        {
            try
            {
                _providerWarmup.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _agent?.Dispose();
        _providerWarmupCancellation?.Dispose();
        _desktop.Dispose();
    }

    private static async Task WarmProviderAsync(
        IWarmablePcModelProvider provider,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await provider.WarmAsync(cancellationToken).ConfigureAwait(false);
            DriverLog.Info(
                "agent.provider_warmed",
                "The internal model provider finished background startup.",
                data: new
                {
                    elapsed_us = (long)(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000),
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DriverLog.Warning(
                "agent.provider_warmup_failed",
                "Background model-provider startup failed; the first PC run will retry initialization.",
                exception: exception);
        }
    }
}
