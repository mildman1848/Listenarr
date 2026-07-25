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
        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
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
                sourceFile,
                destFile,
                pathLock.SourceIdentity,
                pathLock.DestinationIdentity);
            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Completed)
            {
                return true;
            }

            if (recoveryOutcome == FileMoveClaimRecoveryOutcome.Blocked)
            {
                return false;
            }

            return await MoveFileWithLocksAsync(
                sourceFile,
                destFile,
                pathLock.SourceIdentity,
                pathLock.DestinationIdentity);
        }

        private async Task<bool> MoveFileWithLocksAsync(
            string sourceFile,
            string destFile,
            string sourceIdentity,
            string destinationIdentity)
        {
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
                sourceFile,
                destFile,
                sourceIdentity,
                destinationIdentity);
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
                sourceFile,
                destFile,
                sourceIdentity,
                destinationIdentity);
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

            var robocopyFallback = await TryRobocopyFileMoveFallbackAsync(
                sourceFile,
                destFile,
                sourceIdentity,
                destinationIdentity);
            if (robocopyFallback == FileMoveFallbackOutcome.Success)
            {
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Verified robocopy fallback");
                return true;
            }

            LogMutation(
                FileMutationOutcome.Failed,
                FileAction.Move,
                sourceFile,
                destFile,
                robocopyFallback == FileMoveFallbackOutcome.SourceRetained
                    ? "Robocopy published the destination but the verified source could not be removed"
                    : "No verified file move fallback completed");
            return false;
        }

    }
}
