using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public static class MoveManifestIdentity
{
    public const int Version = 4;

    public static string CreateDeduplicationKey(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<MoveJobEntry> entries) =>
        CreateDeduplicationKeyCore(
            audiobookId,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            entries.Select(ToIdentityEntry));

    public static string CreateDeduplicationKey(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<MoveSourceManifestEntry> entries) =>
        CreateDeduplicationKeyCore(
            audiobookId,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            entries.Select(ToIdentityEntry));

    public static bool SourceManifestsMatch(
        IEnumerable<MoveSourceManifestEntry> currentEntries,
        IEnumerable<MoveJobEntry> persistedEntries,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        ArgumentNullException.ThrowIfNull(persistedEntries);
        return string.Equals(
            ComputeManifestDigest(
                currentEntries.Select(ToIdentityEntry),
                semantics),
            ComputeManifestDigest(
                persistedEntries.Select(ToIdentityEntry),
                semantics),
            StringComparison.Ordinal);
    }

    private static string CreateDeduplicationKeyCore(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<ManifestIdentityEntry> entries)
    {
        var sourceKey = FileSystemPathIdentity.CreateKey(
            $"move-source:{audiobookId}",
            source,
            sourceIdentity.Semantics,
            Version);
        var targetKey = FileSystemPathIdentity.CreateKey(
            $"move-target:{audiobookId}",
            target,
            targetIdentity.Semantics,
            Version);
        var manifestDigest = ComputeManifestDigest(
            entries,
            sourceIdentity.Semantics);
        return $"{sourceKey}:{targetKey}:{manifestDigest}";
    }

    private static string ComputeManifestDigest(
        IEnumerable<ManifestIdentityEntry> entries,
        FileSystemPathSemantics semantics)
    {
        var canonical = string.Join(
            '\n',
            entries
                .Select(entry => NormalizeEntry(entry, semantics))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ThenBy(entry => entry.EntryType)
                .Select(entry => string.Join(
                    '|',
                    entry.RelativePath,
                    (int)entry.EntryType,
                    entry.Length,
                    entry.LastWriteTimeUtc.Ticks,
                    entry.Sha256 ?? string.Empty)));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ManifestIdentityEntry NormalizeEntry(
        ManifestIdentityEntry entry,
        FileSystemPathSemantics semantics)
    {
        var relativePath = NormalizeRelativePath(
            entry.RelativePath,
            semantics);
        if (entry.EntryType == MoveJobEntryType.Directory)
        {
            // Directory timestamps are not ownership evidence and may change when
            // unrelated content appears in a shared source tree.
            return new ManifestIdentityEntry(
                relativePath,
                entry.EntryType,
                0,
                DateTime.UnixEpoch,
                null);
        }

        return new ManifestIdentityEntry(
            relativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256?.ToUpperInvariant());
    }

    private static string NormalizeRelativePath(
        string relativePath,
        FileSystemPathSemantics semantics)
    {
        var normalized = relativePath.Normalize(NormalizationForm.FormC);
        if (semantics.Syntax == FileSystemPathSyntax.Windows)
        {
            normalized = normalized.Replace('/', '\\');
        }

        return semantics.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static ManifestIdentityEntry ToIdentityEntry(
        MoveJobEntry entry) =>
        new(
            entry.RelativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256);

    private static ManifestIdentityEntry ToIdentityEntry(
        MoveSourceManifestEntry entry) =>
        new(
            entry.RelativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256);

    private sealed record ManifestIdentityEntry(
        string RelativePath,
        MoveJobEntryType EntryType,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);
}
