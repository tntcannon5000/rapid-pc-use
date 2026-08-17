namespace RapidPcUse.Agent;

internal sealed class VisualMemory
{
    internal Observation? Current { get; private set; }

    internal int RetainedImageCount => Current?.Frames.Count ?? 0;

    internal void Replace(Observation observation) => Current = observation;

    internal void Clear() => Current = null;
}
