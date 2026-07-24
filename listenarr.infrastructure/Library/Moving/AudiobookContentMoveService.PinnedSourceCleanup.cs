using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task MoveSourceFileToPinnedQuarantineAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string sourceFile,
        string quarantineFile,
        string quarantineRoot,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        var (directorySegments, fileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        try
        {
            using var sourcePath = PinnedMoveDirectoryPath.OpenExisting(
                source,
                directorySegments);
            using var quarantinePath = await PinnedMoveDirectoryPath.OpenOrCreateAsync(
                quarantineRoot,
                directorySegments,
                () => EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken));
            ValidatePinnedParentPath(
                sourcePath.Current.FullPath,
                sourceFile,
                sourceSemantics,
                "source cleanup");
            ValidatePinnedParentPath(
                quarantinePath.Current.FullPath,
                quarantineFile,
                sourceSemantics,
                "quarantine cleanup");

            using var sourceEntry = sourcePath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed after validation: {manifestEntry.RelativePath}");
            }

            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforeSourceFilePublication);
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed at publication: {manifestEntry.RelativePath}");
            }

            sourceEntry.MoveTo(quarantinePath.Current, fileName);
            if (!await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine publication changed bytes: {manifestEntry.RelativePath}");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The source file could not be quarantined through pinned directory handles "
                + $"for '{manifestEntry.RelativePath}': {exception.Message}");
        }
    }

    private async Task DeletePinnedQuarantineFileAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string quarantineFile,
        string quarantineRoot,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        var (directorySegments, fileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        try
        {
            using var quarantinePath = PinnedMoveDirectoryPath.OpenExisting(
                quarantineRoot,
                directorySegments);
            ValidatePinnedParentPath(
                quarantinePath.Current.FullPath,
                quarantineFile,
                sourceSemantics,
                "quarantine deletion");
            using var quarantineEntry = quarantinePath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            if (!quarantineEntry.VisiblePathMatches()
                || !await quarantineEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine entry changed before deletion: {manifestEntry.RelativePath}");
            }

            quarantinePath.EnsureVisibleHierarchy();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforeQuarantineFileRemoval);
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            quarantinePath.EnsureVisibleHierarchy();
            if (!quarantineEntry.VisiblePathMatches()
                || !await quarantineEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine entry changed at deletion: {manifestEntry.RelativePath}");
            }

            quarantineEntry.Delete();
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The quarantine file could not be removed through pinned directory handles: {exception.Message}");
        }
    }

    private static void ValidatePinnedParentPath(
        string pinnedParent,
        string expectedFile,
        FileSystemPathSemantics semantics,
        string description)
    {
        var expectedParent = Path.GetDirectoryName(Path.GetFullPath(expectedFile));
        if (string.IsNullOrWhiteSpace(expectedParent)
            || !FileSystemPathIdentity.AreEquivalent(
                Path.GetFullPath(pinnedParent),
                expectedParent,
                semantics))
        {
            throw new MoveNeedsAttentionException(
                $"The pinned {description} parent does not match the manifest path.");
        }
    }

    private static (IReadOnlyList<string> DirectorySegments, string FileName)
        SplitPinnedRelativeFilePath(
            string relativePath,
            FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry has no relative path.");
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var lastSeparator = relativePath.LastIndexOfAny(separators);
        var fileName = lastSeparator < 0
            ? relativePath
            : relativePath[(lastSeparator + 1)..];
        var directoryPart = lastSeparator < 0
            ? string.Empty
            : relativePath[..lastSeparator];
        var segments = directoryPart.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(fileName)
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry contains an invalid cleanup path segment.");
        }

        return (segments, fileName);
    }

}
