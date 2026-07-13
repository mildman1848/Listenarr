/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task FinalizeMoveAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            result.Source,
            result.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        if (!result.SourceCleanupCompleted)
        {
            throw new InvalidOperationException(
                "Move finalization cannot run before source cleanup completes.");
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.Finalizing,
            cancellationToken);

        if (request.DeleteEmptySource && !Directory.Exists(result.Source))
        {
            var nearestExistingAncestor = FindNearestExistingAncestor(result.Source);
            var hasEmptyAncestorToPrune = nearestExistingAncestor != null
                && !IsFilesystemRoot(nearestExistingAncestor, request.SourceSemantics)
                && !Directory.EnumerateFileSystemEntries(nearestExistingAncestor).Any();
            if (hasEmptyAncestorToPrune
                && string.IsNullOrWhiteSpace(request.SourceCleanupBoundary))
            {
                throw new MoveNeedsAttentionException(
                    "Files were moved successfully, but empty source-parent cleanup could not be completed safely because no source cleanup boundary is available.");
            }

            if (!string.IsNullOrWhiteSpace(request.SourceCleanupBoundary))
            {
                // Keep the recovery marker until pruning succeeds so transient filesystem
                // failures remain retryable instead of leaving an orphaned empty folder.
                await RemoveEmptySourceAncestorsAsync(
                    request,
                    result.Source,
                    result.Target,
                    request.SourceCleanupBoundary,
                    request.SourceSemantics,
                    cancellationToken);
            }
        }

        var tempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await TryDeletePublishedTempOwnershipMarkerAsync(
            tempOwnership,
            request,
            result.Source,
            result.Target,
            cancellationToken);
    }

    public async Task CleanupCompletedMoveArtifactsAsync(
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult result,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            result.Source,
            result.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        if (!result.SourceCleanupCompleted)
        {
            throw new InvalidOperationException(
                "Completed move artifacts cannot be cleaned before source cleanup completes.");
        }

        VerifySourceCleanupState(request, result.Source, result.Target);
        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "Completed move artifact cleanup requires a persisted manifest.");
        }

        ValidateTargetManifest(
            result.Target,
            manifest,
            request.TargetSemantics);
        var publishedTempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            publishedTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false);
        await VerifyPublishedManifestAsync(
            result.Target,
            manifest,
            request.TargetSemantics,
            cancellationToken);
        VerifySourceCleanupState(request, result.Source, result.Target);

        if (!File.Exists(result.RecoveryMarkerPath))
        {
            await UpdateJobPhaseAsync(
                request.JobId,
                request.LeaseToken,
                MoveJobPhase.CleaningArtifacts,
                cancellationToken);
            await RetainTargetScaffoldingAsync(request, cancellationToken);
            return;
        }

        ValidateMoveTargetRoot(result.Target);
        var recoveryMarker = ReadRecoveryMarker(result.RecoveryMarkerPath);
        if (recoveryMarker == null)
        {
            throw new MoveNeedsAttentionException(
                "Completed move artifact cleanup requires a structured recovery marker.");
        }

        ValidateRecoveryMarker(
            recoveryMarker,
            request,
            result.Source,
            result.Target);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        ValidateMoveTargetRoot(result.Target);
        ValidateRecoveryMarker(
            ReadRecoveryMarker(result.RecoveryMarkerPath),
            request,
            result.Source,
            result.Target);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        if ((File.GetAttributes(result.RecoveryMarkerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The completed recovery marker became a symbolic link or reparse point.");
        }

        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.CleaningArtifacts,
            cancellationToken);
        faultInjector?.OnCompletedArtifactCleanup(
            request.JobId,
            CompletedArtifactCleanupFaultPoint.BeforeRecoveryMarkerDelete);
        VerifySourceCleanupState(request, result.Source, result.Target);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        ValidateMoveTargetRoot(result.Target);
        var finalTempOwnership = await TryValidatePublishedTempOwnershipAsync(
            result.Target,
            request,
            result.Source,
            result.Target,
            cancellationToken);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            finalTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false);
        await VerifyPublishedManifestAsync(
            result.Target,
            manifest,
            request.TargetSemantics,
            cancellationToken);
        faultInjector?.OnCompletedArtifactCleanup(
            request.JobId,
            CompletedArtifactCleanupFaultPoint.BeforeFinalDestinationOwnershipValidation);
        await EnsureMutationAuthorizedAsync(
            request,
            result.Source,
            result.Target,
            cancellationToken);
        VerifySourceCleanupState(request, result.Source, result.Target);
        ValidateMoveTargetRoot(result.Target);
        ValidateExistingDestinationContents(
            result.Source,
            result.Target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            finalTempOwnership,
            quarantineOwnership: null,
            allowPartialFiles: false);
        ValidateRecoveryMarkerLocation(
            result.RecoveryMarkerPath,
            result.Target,
            request.TargetSemantics);
        ValidateRecoveryMarker(
            ReadRecoveryMarker(result.RecoveryMarkerPath),
            request,
            result.Source,
            result.Target);
        if (!File.Exists(result.RecoveryMarkerPath)
            || (File.GetAttributes(result.RecoveryMarkerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The completed recovery marker changed before deletion.");
        }
        File.Delete(result.RecoveryMarkerPath);
        await RetainTargetScaffoldingAsync(request, cancellationToken);
    }

    public async Task MarkCompletionRecordingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await UpdateJobPhaseAsync(
            request.JobId,
            request.LeaseToken,
            MoveJobPhase.RecordingCompletion,
            cancellationToken);
    }

    private static string? FindNearestExistingAncestor(string source)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(source));
        while (current != null)
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private async Task RemoveEmptyDirectoryTreeAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string directory,
        string boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var current = directory;
        while (Directory.Exists(current)
            && !FileSystemPathIdentity.AreEquivalent(
                current,
                boundary,
                semantics))
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    current,
                    [boundary],
                    out current,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(reason);
            }

            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            faultInjector?.OnMoveFinalization(
                request.JobId,
                MoveFinalizationFaultPoint.BeforeSourceAncestorDelete);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            ValidateExistingMoveDirectory(current, "source ancestor cleanup directory");
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            Directory.Delete(current, false);
            current = Path.GetDirectoryName(current) ?? boundary;
        }
    }

    private static bool IsSourceCleanupBoundary(
        string path,
        string? boundary,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, boundary, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                $"The source cleanup boundary is invalid: {exception.Message}");
        }
    }

    private async Task RemoveEmptySourceAncestorsAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string? boundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return;
        }

        var fullBoundary = Path.GetFullPath(boundary);
        var current = Path.GetDirectoryName(Path.GetFullPath(source));
        while (current != null
            && FileSystemPathIdentity.IsSameOrInside(current, fullBoundary, semantics))
        {
            if (FileSystemPathIdentity.AreEquivalent(current, fullBoundary, semantics))
            {
                return;
            }

            if (Directory.Exists(current))
            {
                await RemoveEmptyDirectoryTreeAsync(
                    request,
                    source,
                    target,
                    current,
                    fullBoundary,
                    semantics,
                    cancellationToken);
                return;
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
