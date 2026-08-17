using RapidPcUse.Agent;
using RapidPcUse.Agent.Providers;

namespace RapidPcUse;

internal sealed class RapidPcHost : IDisposable
{
    private readonly DesktopController _desktop = new();
    private readonly PcAgentLoop? _agent;
    private readonly McpServer _mcp;

    internal RapidPcHost()
    {
        try
        {
            var options = PcAgentOptions.FromEnvironment();
            if (options.Enabled)
            {
                _agent = new PcAgentLoop(_desktop, ProviderFactory.Create(options), options);
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
        _agent?.Dispose();
        _desktop.Dispose();
    }
}
