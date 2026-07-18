using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private async Task<DirectoryMovePlanResult> BuildDirectoryMovePlanAsync(
        Audiobook audiobook,
        string sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var targetOwner = new Audiobook
        {
            Id = audiobook.Id,
            BasePath = targetBasePath
        };
        var ownershipKeys = new HashSet<string>(StringComparer.Ordinal);
        var updates = new List<DirectoryFileUpdate>();

        foreach (var file in audiobook.Files ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return DirectoryMovePlanResult.Failed(
                    "A tracked audiobook file path is missing.",
                    conflict: true);
            }

            if (!TryRewriteStoredFilePath(
                    file.Path,
                    sourceBasePath,
                    targetBasePath,
                    sourceSemantics,
                    targetSemantics,
                    out var storedPath,
                    out var physicalPath))
            {
                return DirectoryMovePlanResult.Failed(
                    "A tracked audiobook file path is outside the expected source folder.",
                    conflict: true);
            }

            var identity = await _filePathIdentityResolver.ResolveAsync(
                targetOwner,
                physicalPath,
                cancellationToken);
            if (identity.State != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(identity.OwnershipKey))
            {
                return DirectoryMovePlanResult.Failed(
                    identity.Reason ?? "A destination audiobook file identity is unavailable.");
            }

            if (!ownershipKeys.Add(identity.OwnershipKey))
            {
                return DirectoryMovePlanResult.Failed(
                    "The folder move would create duplicate audiobook file destinations.");
            }

            var ownership = await _audiobookFileRepository.CheckOwnershipAsync(
                audiobook.Id,
                file.Id,
                identity,
                cancellationToken);
            if (ownership.Outcome != AudiobookFileOwnershipCheckOutcome.Available)
            {
                return DirectoryMovePlanResult.Failed(
                    ownership.Reason ?? "A destination audiobook file identity is already owned.",
                    ownership.Outcome is
                        AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook or
                        AudiobookFileOwnershipCheckOutcome.IdentityConflict);
            }

            updates.Add(new DirectoryFileUpdate(file, storedPath, identity));
        }

        string? rewrittenLegacyPath = null;
        if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            if (!TryRewriteStoredFilePath(
                    audiobook.FilePath,
                    sourceBasePath,
                    targetBasePath,
                    sourceSemantics,
                    targetSemantics,
                    out rewrittenLegacyPath,
                    out _))
            {
                return DirectoryMovePlanResult.Failed(
                    "The legacy audiobook file path is outside the expected source folder.",
                    conflict: true);
            }
        }

        return new DirectoryMovePlanResult(updates, rewrittenLegacyPath, null, false);
    }

    private static bool TryRewriteStoredFilePath(
        string storedPath,
        string sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        out string rewrittenStoredPath,
        out string physicalTargetPath)
    {
        rewrittenStoredPath = storedPath;
        physicalTargetPath = string.Empty;
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                storedPath,
                sourceSemantics.Syntax,
                out _))
        {
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    sourceBasePath,
                    storedPath,
                    sourceSemantics,
                    out var relativePath))
            {
                return false;
            }

            var convertedRelative = FileSystemPathIdentity.ConvertRelativePathSyntax(
                relativePath,
                sourceSemantics.Syntax,
                targetSemantics.Syntax);
            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    targetBasePath,
                    convertedRelative,
                    targetSemantics,
                    out physicalTargetPath))
            {
                return false;
            }

            rewrittenStoredPath = physicalTargetPath;
            return true;
        }

        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
        {
            return false;
        }

        var relativeStoredPath = FileSystemPathIdentity.ConvertRelativePathSyntax(
            storedPath,
            sourceSemantics.Syntax,
            targetSemantics.Syntax);
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                targetBasePath,
                relativeStoredPath,
                targetSemantics,
                out physicalTargetPath))
        {
            return false;
        }

        rewrittenStoredPath = relativeStoredPath;
        return true;
    }

    private sealed record DirectoryFileUpdate(
        AudiobookFile File,
        string StoredPath,
        AudiobookFilePathIdentity Identity);

    private sealed record DirectoryMovePlanResult(
        IReadOnlyList<DirectoryFileUpdate> FileUpdates,
        string? LegacyFilePath,
        string? Error,
        bool Conflict)
    {
        public bool Success => Error == null;

        public static DirectoryMovePlanResult Failed(
            string error,
            bool conflict = false) =>
            new([], null, error, conflict);
    }
}
