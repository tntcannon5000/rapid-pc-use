namespace RapidPcUse.Agent.Providers;

internal static class ProviderFactory
{
    internal static IPcModelProvider Create(PcAgentOptions options)
        => options.Provider switch
        {
            "codex" => new CodexAppServerProvider(options),
            "openai" => new OpenAiResponsesProvider(
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
                options),
            "broker" => new BrokeredModelProvider(options),
            _ => throw new InvalidOperationException("The configured PC agent provider is not registered."),
        };
}
