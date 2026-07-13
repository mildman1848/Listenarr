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

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                source,
                out var files,
                out var directories,
                out _))
        {
            return false;
        }

        var target = Path.GetFullPath(targetPath);
        if (!IsSameOrInside(target, source, semantics))
        {
            return files.Count == 0 && directories.Count == 0;
        }

        return files
            .Concat(directories)
            .All(entry =>
                IsSameOrInside(entry, target, semantics)
                || IsSameOrInside(target, entry, semantics)
                || IsScaffoldMarkerOnTargetSpine(entry, target, semantics));
    }

    private static void VerifySourceCleanupState(
        AudiobookContentMoveRequest request,
        string sourcePath,
        string targetPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(targetPath);
        if (!Directory.Exists(source))
        {
            return;
        }

        ValidateExistingMoveDirectory(source, "source cleanup verification directory");
        var targetInsideSource = IsSameOrInside(
            target,
            source,
            request.SourceSemantics);
        if (targetInsideSource)
        {
            if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    source,
                    out var files,
                    out var directories,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(
                    $"The completed move source could not be verified safely: {reason}");
            }

            var unexpectedEntry = files
                .Concat(directories)
                .FirstOrDefault(entry =>
                    !IsSameOrInside(entry, target, request.SourceSemantics)
                    && !IsSameOrInside(target, entry, request.SourceSemantics)
                    && !IsOwnedScaffoldMarker(entry, request, target));
            if (unexpectedEntry != null)
            {
                throw new MoveNeedsAttentionException(
                    $"The completed move source contains unexpected content: {Path.GetRelativePath(source, unexpectedEntry)}");
            }

            return;
        }

        if (Directory.EnumerateFileSystemEntries(source).Any())
        {
            throw new MoveNeedsAttentionException(
                "The completed move source contains recreated or uncleared content.");
        }

        if (request.DeleteEmptySource
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
