using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<ValidatedQuarantineOwnership> RevalidateSourceToQuarantineMoveAsync(
        string source,
        string target,
        string sourceFile,
        string quarantineFile,
        string quarantineRoot,
        string sourceParent,
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobEntry manifestEntry,
        IReadOnlyCollection<MoveJobEntry> manifest,
        ValidatedTempOwnership? publishedTempOwnership,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(
            jobId,
            leaseToken,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
        ValidateMoveSourceRoot(source);
        if (!FileSystemSafety.TryValidateMutationTarget(
                sourceFile,
                [source],
                out sourceFile,
                out var sourceReason))
        {
            throw new MoveNeedsAttentionException(sourceReason);
        }

        if (!File.Exists(sourceFile)
            || (File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                $"Source cleanup entry is missing or linked: {manifestEntry.RelativePath}");
        }

        if (!await FileMatchesManifestAsync(
                sourceFile,
                manifestEntry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"Source cleanup entry changed after planning: {manifestEntry.RelativePath}");
        }

        var ownership = await ValidateOwnedQuarantineDirectoryAsync(
            quarantineRoot,
            sourceParent,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        ValidateExistingDestinationContents(
            source,
            target,
            manifest,
            jobId,
            targetSemantics,
            publishedTempOwnership,
            ownership,
            allowPartialFiles: false);
        ValidateQuarantineMutationPath(ownership, quarantineFile);
        if (File.Exists(quarantineFile) || Directory.Exists(quarantineFile))
        {
            throw new MoveNeedsAttentionException(
                $"The quarantine destination appeared before cleanup: {manifestEntry.RelativePath}");
        }

        return ownership;
    }

    private async Task<ValidatedQuarantineOwnership> RevalidateQuarantineDeleteAsync(
        string source,
        string target,
        string quarantineFile,
        string quarantineRoot,
        string sourceParent,
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobEntry manifestEntry,
        IReadOnlyCollection<MoveJobEntry> manifest,
        ValidatedTempOwnership? publishedTempOwnership,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(
            jobId,
            leaseToken,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        var ownership = await ValidateOwnedQuarantineDirectoryAsync(
            quarantineRoot,
            sourceParent,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
        ValidateExistingDestinationContents(
            source,
            target,
            manifest,
            jobId,
            targetSemantics,
            publishedTempOwnership,
            ownership,
            allowPartialFiles: false);
        ValidateQuarantineMutationPath(ownership, quarantineFile);
        if (!File.Exists(quarantineFile)
            || (File.GetAttributes(quarantineFile) & FileAttributes.ReparsePoint) != 0
            || !await FileMatchesManifestAsync(
                quarantineFile,
                manifestEntry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"Quarantined source bytes changed before deletion: {manifestEntry.RelativePath}");
        }

        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                target,
                manifestEntry.RelativePath,
                targetSemantics,
                out var targetFile))
        {
            throw new MoveNeedsAttentionException(
                $"Published target path escaped before source deletion: {manifestEntry.RelativePath}");
        }

        ValidateCopyMutationPath(targetFile, target);
        if (!File.Exists(targetFile)
            || (File.GetAttributes(targetFile) & FileAttributes.ReparsePoint) != 0
            || !await FileMatchesManifestAsync(
                targetFile,
                manifestEntry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"Published target bytes changed before source deletion: {manifestEntry.RelativePath}");
        }

        await EnsureMutationAuthorizedAsync(
            jobId,
            leaseToken,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        ownership = await ValidateOwnedQuarantineDirectoryAsync(
            quarantineRoot,
            sourceParent,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
        ValidateQuarantineMutationPath(ownership, quarantineFile);
        ValidateCopyMutationPath(targetFile, target);
        if (!File.Exists(quarantineFile)
            || (File.GetAttributes(quarantineFile) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(quarantineFile).Length != manifestEntry.Length
            || !File.Exists(targetFile)
            || (File.GetAttributes(targetFile) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(targetFile).Length != manifestEntry.Length)
        {
            throw new MoveNeedsAttentionException(
                $"Source or target bytes changed after lease revalidation: {manifestEntry.RelativePath}");
        }

        return ownership;
    }

    private static void DeleteValidatedEmptySourceDirectory(
        string source,
        string directory,
        FileSystemPathSemantics sourceSemantics)
    {
        ValidateMoveSourceRoot(source);
        var reason = string.Empty;
        if (!FileSystemPathIdentity.IsSameOrInside(
                Path.GetFullPath(directory),
                Path.GetFullPath(source),
                sourceSemantics)
            || !FileSystemSafety.TryValidateMutationTarget(
                directory,
                [source],
                out directory,
                out reason))
        {
            throw new MoveNeedsAttentionException(
                string.IsNullOrWhiteSpace(reason)
                    ? "An empty source directory escaped the persisted source root."
                    : reason);
        }

        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new MoveNeedsAttentionException(
                "An empty source directory changed before deletion.");
        }

        Directory.Delete(directory, recursive: false);
    }

    private static void DeleteValidatedEmptyQuarantineDirectory(
        ValidatedQuarantineOwnership ownership,
        string directory)
    {
        ValidateQuarantineMutationPath(ownership, directory);
        if (!Directory.Exists(directory)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new MoveNeedsAttentionException(
                "An empty quarantine directory changed before deletion.");
        }

        Directory.Delete(directory, recursive: false);
    }

}
