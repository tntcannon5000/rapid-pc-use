namespace RapidPcUse;

internal sealed class RapidPcHost : IDisposable
{
    private readonly DesktopController _desktop = new();
    private readonly McpServer _mcp;

    internal RapidPcHost()
    {
        _mcp = new McpServer(_desktop, Console.In, Console.Out);
    }

    internal void Run() => _mcp.Run();

    public void Dispose() => _desktop.Dispose();
}
