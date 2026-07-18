using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal static bool CanAttemptFinalizedMoveVerification(
        string sourcePath,
        string targetPath,
        FileSystemPathSemantics semantics)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source))
        {
            return true;
        }

        // Ordinary foreign content may remain in a shared source directory.
        // The manifest-aware finalized verifier decides whether owned paths were
        // cleaned; this preflight only rejects an unsafe linked source tree.
        return FileSystemSafety.TryEnumerateTreeWithoutLinks(
            source,
            out _,
            out _,
            out _);
    }

    private static void VerifySourceCleanupState(
        AudiobookContentMoveRequest request,
        string sourcePath,
        string targetPath,
        IReadOnlyCollection<MoveJobEntry> manifest)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source))
        {
            return;
        }

        ValidateExistingMoveDirectory(source, "source cleanup verification directory");
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                source,
                out var remainingFiles,
                out var remainingDirectories,
                out var reason))
        {
            throw new MoveNeedsAttentionException(
                $"The completed move source could not be verified safely: {reason}");
        }

        foreach (var entry in manifest.Where(entry => !IsRootManifestEntry(entry)))
        {
            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    source,
                    entry.RelativePath,
                    request.SourceSemantics,
                    out var sourceEntry))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted source manifest entry escaped its authorized source root during cleanup verification.");
            }

            if (entry.EntryType == MoveJobEntryType.File)
            {
                if (File.Exists(sourceEntry) || Directory.Exists(sourceEntry))
                {
                    throw new MoveNeedsAttentionException(
                        $"The completed move source contains a recreated or uncleared owned file path: {entry.RelativePath}");
                }

                continue;
            }

            if (entry.EntryType != MoveJobEntryType.Directory)
            {
                throw new MoveNeedsAttentionException(
                    "The persisted source manifest contains an unsupported entry type.");
            }

            if (File.Exists(sourceEntry))
            {
                throw new MoveNeedsAttentionException(
                    $"The completed move source directory changed into a file: {entry.RelativePath}");
            }

            if (Directory.Exists(sourceEntry)
                && !Directory.EnumerateFileSystemEntries(sourceEntry).Any())
            {
                throw new MoveNeedsAttentionException(
                    $"The completed move source contains an uncleared empty owned directory: {entry.RelativePath}");
            }
        }

        var target = Path.GetFullPath(targetPath);
        var targetInsideSource = IsSameOrInside(
            target,
            source,
            request.SourceSemantics);
        var ordinaryRemainingEntries = remainingFiles
            .Concat(remainingDirectories)
            .Where(entry => !targetInsideSource
                || (!IsSameOrInside(entry, target, request.SourceSemantics)
                    && !IsSameOrInside(target, entry, request.SourceSemantics)
                    && !IsOwnedScaffoldMarker(entry, request, target)))
            .ToList();

        if (ordinaryRemainingEntries.Count == 0
            && request.DeleteEmptySource
            && !targetInsideSource
            && !IsSourceCleanupBoundary(
                source,
                request.SourceCleanupBoundary,
                request.SourceSemantics))
        {
            throw new MoveNeedsAttentionException(
                "The completed move source directory was recreated after cleanup.");
        }
    }

    private static bool IsOwnedScaffoldMarker(
        string entry,
        AudiobookContentMoveRequest request,
        string target)
    {
        if (!IsScaffoldMarkerOnTargetSpine(entry, target, request.SourceSemantics))
        {
            return false;
        }

        var publishedRoot = Path.GetDirectoryName(entry);
        if (string.IsNullOrWhiteSpace(publishedRoot))
        {
            return false;
        }

        try
        {
            ValidateScaffoldMarker(
                ReadScaffoldMarker(publishedRoot),
                request.JobId,
                target,
                publishedRoot,
                request.SourceSemantics);
            return true;
        }
        catch (MoveNeedsAttentionException)
        {
            return false;
        }
    }

    private static bool IsScaffoldMarkerOnTargetSpine(
        string entry,
        string target,
        FileSystemPathSemantics semantics)
    {
        if (!string.Equals(
                Path.GetFileName(entry),
                ScaffoldOwnerFileName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(entry);
        return !string.IsNullOrWhiteSpace(directory)
            && !FileSystemPathIdentity.AreEquivalent(directory, target, semantics)
            && FileSystemPathIdentity.IsSameOrInside(target, directory, semantics);
    }
}
