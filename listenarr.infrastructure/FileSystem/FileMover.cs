/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    internal enum FileMutationOutcome
    {
        Success,
        Skipped,
        Blocked,
        Failed
    }

    internal sealed record FileMutationResult(
        FileMutationOutcome Outcome,
        FileAction Action,
        string SourcePath,
        string? DestinationPath,
        string? Reason = null);

    public partial class FileMover : IFileMover
    {
        // .NET has no managed BCL equivalent for hardlink creation.
        // LibraryImport (source-generated P/Invoke, .NET 7+) is used instead of the legacy
        // DllImport attribute to minimise unmanaged interop overhead and satisfy CA1060/CA2101.
        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET.")]
        private static partial bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "No managed BCL equivalent for hardlink creation exists in .NET.")]
        private static partial int LinkNative(string oldpath, string newpath);

        private readonly ILogger<FileMover> _logger;
        private readonly IProcessRunner? _processRunner;
        private readonly FileMoverOptions _options;
        private readonly IFileSystemSemanticsResolver? _semanticsResolver;

        internal Func<Task>? AfterSourceStateCreatedForTestAsync { get; init; }
        internal Func<string, string, Task>? AfterSourceQuarantinedForTestAsync { get; init; }
        internal Func<Task>? AfterDestinationStateCreatedForTestAsync { get; init; }
        internal Func<string, string, Task>? AfterDestinationQuarantinedForTestAsync { get; init; }
        internal Func<Task>? AfterSourceRetirementCommittedForTestAsync { get; init; }
        internal Func<string, Task>? AfterDestinationPublishedForTestAsync { get; init; }
        internal Func<Task>? AfterSourceClaimDeletedForTestAsync { get; init; }
        internal Func<Task>? AfterFileMoveStateCleanedForTestAsync { get; init; }
        internal Func<Task>? AfterDirectoryCopyPreflightForTestAsync { get; init; }
        internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
        internal Action? BeforeDirectoryTreePreflightForTest { get; init; }

        public FileMover(
            ILogger<FileMover> logger,
            IProcessRunner? processRunner = null,
            IOptions<FileMoverOptions>? options = null,
            IFileSystemSemanticsResolver? semanticsResolver = null)
        {
            _logger = logger;
            _processRunner = processRunner;
            _options = options?.Value ?? new FileMoverOptions();
            _semanticsResolver = semanticsResolver;
        }

        public async Task<bool> MoveDirectoryAsync(string sourceDir, string destDir)
        {
            var pathEquivalence = await TryDetermineFilesystemPathEquivalenceAsync(
                sourceDir,
                destDir);
            if (pathEquivalence == true)
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceDir,
                    destDir,
                    "Source and destination identify the same directory");
                return true;
            }

            var pathsOverlap = await TryDetermineDirectoryOverlapAsync(sourceDir, destDir);
            if (pathsOverlap == true)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceDir,
                    destDir,
                    "Source and destination directories overlap");
                return false;
            }

            if (!TryRecoverInterruptedCopiedSourceCleanup(
                    sourceDir,
                    out var recoveryReason))
            {
                _logger.LogWarning(
                    "Blocked directory move because interrupted source cleanup could not be recovered: {Reason}",
                    recoveryReason);
                return false;
            }

            var targetExistsBeforeAttempt = Directory.Exists(destDir);
            var fallbackRequired = false;
            Exception? directMoveFailure = null;

            try
            {
                BeforeDirectoryTreePreflightForTest?.Invoke();
                await EnsureDirectoryMoveTargetSafeAsync(sourceDir, destDir, destDir);
                Directory.Move(sourceDir, destDir);
                if (!Directory.Exists(destDir) || Directory.Exists(sourceDir))
                {
                    throw new IOException(
                        "Directory move completed without establishing the expected source and destination state.");
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                directMoveFailure = ex;
                fallbackRequired = true;
            }

            if (!fallbackRequired)
            {
                return false;
            }

            try
            {
                await CopyDirectoryAsync(sourceDir, destDir);
                if (!Directory.Exists(sourceDir))
                {
                    return Directory.Exists(destDir);
                }

                if (!targetExistsBeforeAttempt)
                {
                    return TryRetireCopiedSourceDirectory(
                        sourceDir,
                        destDir,
                        out _);
                }

                _logger.LogWarning(
                    directMoveFailure,
                    "Directory move fell back to copy, but source retirement is blocked because the destination existed before the operation");
                return false;
            }
            catch (Exception fallbackFailure) when (fallbackFailure is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    fallbackFailure,
                    "Directory move fallback failed after direct move failure");
                return false;
            }
        }

        public async Task<bool> CopyDirectoryAsync(string sourceDir, string destDir)
        {
            try
            {
                BeforeDirectoryTreePreflightForTest?.Invoke();
                await EnsureDirectoryCopyTargetSafeAsync(sourceDir, destDir, destDir);
                if (!TryCaptureDirectoryCopySnapshot(sourceDir, out var snapshot, out var snapshotReason)
                    || snapshot == null)
                {
                    throw new IOException(snapshotReason);
                }

                if (AfterDirectoryCopyPreflightForTestAsync != null)
                {
                    await AfterDirectoryCopyPreflightForTestAsync();
                }

                await CopyDirectorySnapshotAsync(snapshot, destDir);
                return await DirectoryCopySnapshotStillMatchesAsync(snapshot, destDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to copy directory {SourceDir} to {DestDir}", sourceDir, destDir);
                return false;
            }
        }

        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
        {
            if (string.Equals(sourceFile, destFile, StringComparison.Ordinal))
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source and destination are the same literal path");
                return true;
            }

            if (await IsFileAliasOperationBlockedAsync(sourceFile, destFile))
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source and destination are linked aliases");
                return false;
            }

            if (!File.Exists(sourceFile))
            {
                LogMutation(
                    FileMutationOutcome.Failed,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source file does not exist");
                return false;
            }

            var sourceIdentity = TryResolveFileEndpointIdentity(sourceFile);
            var destinationIdentity = TryResolveFileEndpointIdentity(destFile);
            if (await IsFileAliasOperationBlockedAsync(sourceFile, destFile))
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source or destination became a linked alias while resolving endpoint identity");
                return false;
            }

            if (destinationIdentity is { Exists: true }
                && sourceIdentity is { Exists: true }
                && destinationIdentity.Value.Equals(sourceIdentity.Value))
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "Source and destination identify the same unlinked file");
                return true;
            }

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

            try
            {
                var destinationDirectory = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                using var lease = await TryAcquireFileMoveLeaseAsync(sourceFile, destFile);
                if (lease == null)
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Move,
                        sourceFile,
                        destFile,
                        "Cross-process move lease could not be acquired");
                    return false;
                }

                if (await IsFileAliasOperationBlockedAsync(sourceFile, destFile))
                {
                    LogMutation(
                        FileMutationOutcome.Blocked,
                        FileAction.Move,
                        sourceFile,
                        destFile,
                        "Source or destination became a linked alias while acquiring the move lease");
                    return false;
                }

                if (await TryMoveFileWithRecoveryAsync(sourceFile, destFile, lease))
                {
                    return true;
                }

                LogMutation(
                    FileMutationOutcome.Failed,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "The durable file move workflow did not complete successfully");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Failed to move file {SourceFile} to {DestFile}", sourceFile, destFile);
                return false;
            }
        }
