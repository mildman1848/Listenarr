/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover
    {
        public Task<bool> MoveFileAsync(string sourceFile, string destFile) =>
            MoveFileAsync(sourceFile, destFile, operationId: null);

        internal async Task<bool> MoveFileAsync(
            string sourceFile,
            string destFile,
            Guid? operationId)
        {
            if (string.Equals(
                    Path.GetFullPath(sourceFile),
                    Path.GetFullPath(destFile),
                    StringComparison.Ordinal))
            {
                return true;
            }

            using var pathLock = await TryAcquireFileMoveGateAsync(
                sourceFile,
                destFile);
            if (pathLock == null)
            {
                return false;
            }

            var recoveryOutcome = await TryRecoverInterruptedFileMoveClaimsAsync(
                pathLock,
                operationId);
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Completed)
            {
                return true;
            }

            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Blocked)
            {
                return false;
            }
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.SourceRecreated)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "A new source generation exists beside committed move state");
                return false;
            }

            return await MoveFileWithLocksAsync(
                pathLock,
                operationId);
        }

        private async Task<bool> MoveFileWithLocksAsync(
            FileMoveGateLease lease,
            Guid? operationId)
        {
            var sourceFile = lease.SourcePath;
            var destFile = lease.DestinationPath;
            var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
                sourceFile,
                destFile);
            if (pathEquivalence == true)
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source and destination identify the same file");
                return true;
            }

            var idempotentOutcome = await TryCompleteIdempotentFileMoveAsync(
                lease,
                operationId);
            if (idempotentOutcome == IdempotentFileMoveOutcome.Completed)
            {
                return true;
            }

            if (idempotentOutcome == IdempotentFileMoveOutcome.SourcePathRecreated)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source path was recreated while completing an idempotent move");
                return false;
            }

            if (pathEquivalence == null
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(destFile))
            {
                _logger.LogWarning(
                    "Blocked copy-and-delete file fallback because filesystem identity or link safety could not prove distinct regular files: {Source} -> {Destination}",
                    LogRedaction.SanitizeFilePath(sourceFile),
                    LogRedaction.SanitizeFilePath(destFile));
                return false;
            }

            var managedFallback = await TryManagedFileMoveFallbackAsync(
                lease,
                operationId);
            if (managedFallback == FileMoveFallbackOutcome.Success)
            {
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Verified copy fallback");
                return true;
            }

            if (managedFallback == FileMoveFallbackOutcome.SourceRetained)
            {
                LogMutation(
                    FileMutationOutcome.Failed,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Destination was published but the verified source could not be removed");
                return false;
            }

            LogMutation(
                FileMutationOutcome.Failed,
                FileAction.Move,
                sourceFile,
                destFile,
                "No verified anchored file move fallback completed");
            return false;
        }

    }
}
