using System.Diagnostics;

namespace RapidPcUse.Agent.Providers;

internal sealed class ReplayPcModelProvider(IEnumerable<PcAgentDecision> decisions) : IPcModelProvider
{
    private readonly Queue<PcAgentDecision> _decisions = new(decisions);

    public string Name => "replay";

    public string Model => "deterministic-replay";

    public Task<PcModelTurnResult> DecideAsync(PcModelTurnRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_decisions.Count == 0)
        {
            throw new InvalidOperationException("The replay provider has no remaining decisions.");
        }

        var started = Stopwatch.GetTimestamp();
        var decision = _decisions.Dequeue();
        var elapsed = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000);
        return Task.FromResult(new PcModelTurnResult(
            decision,
            Name,
            Model,
            0,
            request.Observation.Frames.Count,
            request.Observation.Frames.Sum(frame => frame.Bytes.Length),
            0,
            new ProviderLocalStageTimings(0, 0, 0, 0),
            new ProviderTurnTimings(0, elapsed, elapsed, elapsed, elapsed),
            new ProviderUsage(null, null, null, null)));
    }

    public void Dispose()
    {
    }
}
