using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task CopySourceContentsAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string copyDestination,
        IReadOnlyList<MoveJobEntry> manifest,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        ValidatedTempOwnership? tempOwnership,
        bool directCopyOwnershipValidated,
        CancellationToken cancellationToken)
    {
        ValidateExistingDestinationContents(
            source,
            copyDestination,
            manifest,
            request.JobId,
            targetSemantics,
            tempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: tempOwnership != null || directCopyOwnershipValidated,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);

        foreach (var manifestEntry in manifest.OrderBy(entry => entry.EntryType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRootManifestEntry(manifestEntry))
            {
                ValidateExistingMoveDirectory(copyDestination, "copy destination root");
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                copyDestination,
                manifestEntry.RelativePath,
                targetSemantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    $"Move entry destination escaped target root: {manifestEntry.RelativePath}");
            }

            if (!FileSystemSafety.TryValidateMutationTarget(
                destinationPath,
                [copyDestination],
                out destinationPath,
                out var destinationReason))
            {
                throw new MoveNeedsAttentionException(destinationReason);
            }

            if (manifestEntry.EntryType == MoveJobEntryType.Directory)
            {
                if (Directory.Exists(destinationPath)
                    && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"Move destination directory is a symbolic link or reparse point: {manifestEntry.RelativePath}");
                }

                if (!Directory.Exists(destinationPath))
                {
                    await EnsureMutationAuthorizedAsync(
                        request,
                        source,
                        target,
                        cancellationToken);
                    ValidateCopyMutationPath(destinationPath, copyDestination);
                    Directory.CreateDirectory(destinationPath);
                    ValidateCopyMutationPath(destinationPath, copyDestination);
                    if ((File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new MoveNeedsAttentionException(
                            $"Move destination directory became linked during creation: {manifestEntry.RelativePath}");
                    }
                }

                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                source,
                manifestEntry.RelativePath,
                sourceSemantics,
                out var entry))
            {
                throw new MoveNeedsAttentionException(
                    $"Move entry escaped source root: {manifestEntry.RelativePath}");
            }

            await CopyFileWithRetryAsync(
                request,
                source,
                target,
                entry,
                destinationPath,
                manifestEntry,
                copyDestination,
                tempOwnership != null,
                tempOwnership != null || directCopyOwnershipValidated,
                sourceSemantics,
                cancellationToken);
        }
    }

    private void ValidateExistingDestinationContents(
        string source,
        string destinationRoot,
        IReadOnlyCollection<MoveJobEntry> manifest,
        Guid jobId,
        FileSystemPathSemantics targetSemantics,
        ValidatedTempOwnership? tempOwnership = null,
        ValidatedQuarantineOwnership? quarantineOwnership = null,
        bool allowPartialFiles = true,
        LibraryDirectoryOwnership? targetDirectoryOwnership = null)
    {
        if (!Directory.Exists(destinationRoot))
        {
            return;
        }

        RevalidateTargetDirectoryOwnership(targetDirectoryOwnership);
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
            destinationRoot,
            out var files,
            out var directories,
            out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                expectedPaths.Add(FileSystemPathIdentity.CreateKey(
                    "move-target",
                    destinationRoot,
                    targetSemantics));
                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                destinationRoot,
                entry.RelativePath,
                targetSemantics,
                out var expectedPath))
            {
                throw new MoveNeedsAttentionException("A manifest entry escaped the destination root.");
            }

            expectedPaths.Add(FileSystemPathIdentity.CreateKey("move-target", expectedPath, targetSemantics));
        }

        var markerPath = GetRecoveryMarkerPath(destinationRoot, jobId);
        var partialSuffix = $".listenarr-{jobId:N}.partial";
        var sourceInsideDestination = IsSameOrInside(source, destinationRoot, targetSemantics);

        foreach (var directory in directories)
        {
            if ((quarantineOwnership != null
                    && IsSameOrInside(
                        directory,
                        quarantineOwnership.DirectoryPath,
                        targetSemantics))
                || (sourceInsideDestination
                    && (IsSameOrInside(directory, source, targetSemantics)
                        || IsSameOrInside(source, directory, targetSemantics))))
            {
                continue;
            }

            var key = FileSystemPathIdentity.CreateKey("move-target", directory, targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned directory: {Path.GetRelativePath(destinationRoot, directory)}");
            }
        }

        foreach (var file in files)
        {
            if (IsValidatedTargetOwnershipMarker(
                    file,
                    targetDirectoryOwnership,
                    targetSemantics)
                || FileSystemPathIdentity.AreEquivalent(file, markerPath, targetSemantics)
                || (tempOwnership != null
                    && FileSystemPathIdentity.AreEquivalent(
                        file,
                        tempOwnership.MarkerPath,
                        targetSemantics))
                || (quarantineOwnership != null
                    && IsSameOrInside(
                        file,
                        quarantineOwnership.DirectoryPath,
                        targetSemantics))
                || (sourceInsideDestination && IsSameOrInside(file, source, targetSemantics)))
            {
                continue;
            }

            var isPartialFile = file.EndsWith(partialSuffix, StringComparison.Ordinal);
            if (isPartialFile && !allowPartialFiles)
            {
                throw new MoveNeedsAttentionException(
                    $"Finalized destination contains an incomplete copy artifact: {Path.GetRelativePath(destinationRoot, file)}");
            }

            var expectedFile = isPartialFile
                ? file[..^partialSuffix.Length]
                : file;
            var key = FileSystemPathIdentity.CreateKey("move-target", expectedFile, targetSemantics);
            if (!expectedPaths.Contains(key))
            {
                throw new MoveNeedsAttentionException(
                    $"Destination contains an unowned file: {Path.GetRelativePath(destinationRoot, file)}");
            }
        }
    }

    private static void RejectUnownedPartialArtifacts(
        string target,
        Guid jobId,
        bool hasStructuredRecoveryMarker)
    {
        if (hasStructuredRecoveryMarker || !Directory.Exists(target))
        {
            return;
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                target,
                out var files,
                out _,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var partialSuffix = $".listenarr-{jobId:N}.partial";
        var partial = files.FirstOrDefault(path =>
            path.EndsWith(partialSuffix, StringComparison.Ordinal));
        if (partial != null)
        {
            throw new MoveNeedsAttentionException(
                $"A job-shaped partial file exists without structured move ownership and was preserved: {Path.GetRelativePath(target, partial)}");
        }
    }

    private async Task CopyFileWithRetryAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        string sourceFile,
        string destinationFile,
        MoveJobEntry manifestEntry,
        string destinationRoot,
        bool destinationIsJobOwnedTemp,
        bool destinationHasStructuredOwnership,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        ValidateCopyMutationPath(destinationFile, destinationRoot);
        var destinationDirectory = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
        {
            await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
            ValidateCopyMutationPath(destinationFile, destinationRoot);
            Directory.CreateDirectory(destinationDirectory);
            ValidateCopyMutationPath(destinationFile, destinationRoot);
        }

        var partialFile = destinationFile + $".listenarr-{request.JobId:N}.partial";
        for (var attempt = 1; attempt <= MaxCopyAttempts; attempt++)
        {
            var partialExistedBeforeAttempt = File.Exists(partialFile);
            try
            {
                if (!await FileMatchesManifestAsync(sourceFile, manifestEntry, cancellationToken))
                {
                    throw new MoveNeedsAttentionException(
                        $"Source file no longer matches the persisted move manifest: {manifestEntry.RelativePath}");
                }

                ValidateCopyMutationPath(destinationFile, destinationRoot);
                ValidateCopyMutationPath(partialFile, destinationRoot);

                if (File.Exists(destinationFile))
                {
                    if (await FileMatchesManifestAsync(destinationFile, manifestEntry, cancellationToken))
                    {
                        await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                        await RemoveExistingPartialAsync(
                            partialFile,
                            destinationRoot,
                            manifestEntry,
                            destinationIsJobOwnedTemp,
                            destinationHasStructuredOwnership,
                            () => EnsureMutationAuthorizedAsync(
                                request,
                                sourceRoot,
                                target,
                                cancellationToken),
                            cancellationToken);
                        logger.LogInformation(
                            "Skipping copy for move job {JobId}; destination already matches the persisted manifest: {Destination}",
                            request.JobId,
                            LogRedaction.SanitizeFilePath(destinationFile));
                        return;
                    }

                    if (!destinationIsJobOwnedTemp)
                    {
                        throw new MoveNeedsAttentionException(
                            $"Destination file differs from the move manifest and will not be overwritten: {Path.GetFileName(destinationFile)}");
                    }

                    await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                    DeleteValidatedOwnedFile(destinationFile, destinationRoot);
                }

                if (File.Exists(partialFile))
                {
                    if (!destinationHasStructuredOwnership)
                    {
                        throw new MoveNeedsAttentionException(
                            "A job-shaped partial file exists without structured move ownership.");
                    }

                    ValidateExistingOwnedFile(partialFile, destinationRoot);
                    if (await FileMatchesManifestAsync(partialFile, manifestEntry, cancellationToken))
                    {
                        await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                        faultInjector?.OnCopyMutation(
                            request.JobId,
                            CopyMutationFaultPoint.BeforePartialPublication);
                        ValidateCopyMutationPath(destinationFile, destinationRoot);
                        ValidateExistingOwnedFile(partialFile, destinationRoot);
                        if (!await FileMatchesManifestAsync(
                                partialFile,
                                manifestEntry,
                                cancellationToken))
                        {
                            throw new MoveNeedsAttentionException(
                                $"The partial copy changed before publication: {Path.GetFileName(partialFile)}");
                        }

                        await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                        ValidateCopyMutationPath(destinationFile, destinationRoot);
                        ValidateExistingOwnedFile(partialFile, destinationRoot);
                        if (!await FileMatchesManifestAsync(
                                partialFile,
                                manifestEntry,
                                cancellationToken))
                        {
                            throw new MoveNeedsAttentionException(
                                $"The partial copy changed after lease revalidation: {Path.GetFileName(partialFile)}");
                        }
                        await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                        ValidateCopyMutationPath(destinationFile, destinationRoot);
                        ValidateExistingOwnedFile(partialFile, destinationRoot);
                        File.Move(partialFile, destinationFile, overwrite: false);
                        return;
                    }

                    if (!destinationIsJobOwnedTemp)
                    {
                        throw new MoveNeedsAttentionException(
                            $"A direct-copy partial file does not match the persisted manifest and was preserved: {Path.GetFileName(partialFile)}");
                    }

                    await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                    DeleteValidatedOwnedFile(partialFile, destinationRoot);
                }

                await ValidateSourceCopyPathAsync(
                    sourceRoot,
                    sourceFile,
                    manifestEntry,
                    sourceSemantics,
                    cancellationToken);
                ValidateCopyMutationPath(partialFile, destinationRoot);
                if (File.Exists(partialFile) || Directory.Exists(partialFile))
                {
                    throw new MoveNeedsAttentionException(
                        $"The partial copy destination appeared before copying: {Path.GetFileName(partialFile)}");
                }

                await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                await CopyFileWithLeaseChecksAsync(
                    request,
                    sourceRoot,
                    target,
                    sourceFile,
                    partialFile,
                    cancellationToken);
                await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                PreserveFileMetadata(sourceFile, partialFile);
                if (!await FileMatchesManifestAsync(partialFile, manifestEntry, cancellationToken))
                {
                    await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                    DeleteValidatedOwnedFile(partialFile, destinationRoot);
                    throw new IOException("Temporary move copy failed persisted-manifest verification.");
                }

                ValidateCopyMutationPath(destinationFile, destinationRoot);
                ValidateExistingOwnedFile(partialFile, destinationRoot);
                if (File.Exists(destinationFile))
                {
                    if (await FileMatchesManifestAsync(destinationFile, manifestEntry, cancellationToken))
                    {
                        await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                        DeleteValidatedOwnedFile(partialFile, destinationRoot);
                        return;
                    }

                    throw new MoveNeedsAttentionException(
                        $"Destination file appeared during the move and differs from the manifest: {Path.GetFileName(destinationFile)}");
                }

                await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                faultInjector?.OnCopyMutation(
                    request.JobId,
                    CopyMutationFaultPoint.BeforePartialPublication);
                ValidateExistingOwnedFile(partialFile, destinationRoot);
                if (!await FileMatchesManifestAsync(
                        partialFile,
                        manifestEntry,
                        cancellationToken))
                {
                    throw new MoveNeedsAttentionException(
                        $"The partial copy changed before publication: {Path.GetFileName(partialFile)}");
                }

                await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                ValidateCopyMutationPath(destinationFile, destinationRoot);
                ValidateExistingOwnedFile(partialFile, destinationRoot);
                if (!await FileMatchesManifestAsync(
                        partialFile,
                        manifestEntry,
                        cancellationToken))
                {
                    throw new MoveNeedsAttentionException(
                        $"The partial copy changed after lease revalidation: {Path.GetFileName(partialFile)}");
                }
                await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                ValidateCopyMutationPath(destinationFile, destinationRoot);
                ValidateExistingOwnedFile(partialFile, destinationRoot);
                File.Move(partialFile, destinationFile, overwrite: false);
                return;
            }
            catch (MoveNeedsAttentionException)
            {
                throw;
            }
            catch (IOException exception) when (attempt < MaxCopyAttempts)
            {
                if (!partialExistedBeforeAttempt && File.Exists(partialFile))
                {
                    await EnsureMutationAuthorizedAsync(request, sourceRoot, target, cancellationToken);
                    DeleteValidatedOwnedFile(partialFile, destinationRoot);
                }

                logger.LogWarning(
                    exception,
                    "IO error copying file {File} attempt {Attempt}",
                    LogRedaction.SanitizeFilePath(sourceFile),
                    attempt);
                var delay = TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new IOException($"Failed to copy file after {MaxCopyAttempts} attempts: {sourceFile}");
    }

}
