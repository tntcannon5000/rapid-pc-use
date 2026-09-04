namespace RapidPcUse.Knowledge;

internal sealed record PcKnowledgeEntry(
    string Key,
    string Kind,
    string Subject,
    string Fact,
    string NavigationHint,
    string Source,
    int Confidence,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int Revision);

internal sealed record PcKnowledgeDocument(
    int Version,
    IReadOnlyList<PcKnowledgeEntry> Entries);
