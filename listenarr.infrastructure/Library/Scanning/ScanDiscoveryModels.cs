using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal enum ScanDiscoveryIssueKind
{
    EnumerationFailure,
    LinkSkipped,
    AttributionConflict,
    MetadataUnavailable,
    OutsideStableIdentifierBoundary
}

internal sealed record ScanDiscoveryIssue(
    ScanDiscoveryIssueKind Kind,
    string? Path,
    string Message);

internal sealed record ScanDiscoveryResult(
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> AttributedFiles,
    IReadOnlyDictionary<string, string> ProvenBookBoundaries,
    IReadOnlyList<string> EnumeratedDirectories,
    string? SelectedStableIdentifierBoundary,
    bool HasStableIdentifierBoundaryConflict,
    IReadOnlyList<ScanDiscoveryIssue> Issues)
{
    public bool IsComplete => Issues.All(issue =>
        issue.Kind is not (ScanDiscoveryIssueKind.EnumerationFailure
            or ScanDiscoveryIssueKind.LinkSkipped));

    public bool HasAttributionConflict => Issues.Any(issue =>
        issue.Kind == ScanDiscoveryIssueKind.AttributionConflict);

    public bool CanReconcile => IsComplete;

    public bool CanUpdateBasePath => IsComplete && !HasAttributionConflict;

    public string? CommonProvenBookBoundary(FileSystemPathSemantics semantics)
    {
        if (!string.IsNullOrWhiteSpace(SelectedStableIdentifierBoundary))
        {
            return SelectedStableIdentifierBoundary;
        }

        var boundaries = AttributedFiles
            .Select(path => ProvenBookBoundaries.TryGetValue(path, out var boundary)
                ? boundary
                : null)
            .Where(boundary => !string.IsNullOrWhiteSpace(boundary))
            .Distinct(semantics.Comparer)
            .ToList();
        return boundaries.Count == 1 ? boundaries[0] : null;
    }
}
