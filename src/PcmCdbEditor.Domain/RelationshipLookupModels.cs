namespace PcmCdbEditor.Domain;

public sealed record RelationshipLookupOption(
    SqliteValue Key,
    string? Label,
    string Context,
    string TargetTable,
    string TargetColumn,
    RowIdentity TargetIdentity,
    RowRevision TargetRevision);

public sealed class RelationshipLookupPage
{
    public RelationshipLookupPage(
        IEnumerable<RelationshipLookupOption> options,
        bool hasMore,
        string? unavailableReason = null)
    {
        Options = ModelCollections.Freeze(options);
        HasMore = hasMore;
        UnavailableReason = unavailableReason;
    }

    public IReadOnlyList<RelationshipLookupOption> Options { get; }

    public bool HasMore { get; }

    public string? UnavailableReason { get; }
}

public sealed record RelationshipLookupResult(
    RelationshipResolution Resolution,
    RelationshipLookupOption? Option = null,
    string? UnavailableReason = null);
