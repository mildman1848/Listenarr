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

using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService : IAudiobookFilesystemDeleteService
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly ILibraryDirectoryOwnershipStore _directoryOwnershipStore;
        private readonly ILogger<AudiobookFilesystemDeleteService> _logger;
        private readonly LibraryDirectoryOwnershipBoundaryAuthorizer? _ownershipAuthorizer;

        public AudiobookFilesystemDeleteService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audioFileRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            IFileSystemSemanticsResolver semanticsResolver,
            ILibraryDirectoryOwnershipStore directoryOwnershipStore,
            ILogger<AudiobookFilesystemDeleteService> logger,
            LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer = null)
        {
            _audiobookRepository = audiobookRepository;
            _audioFileRepository = audioFileRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _semanticsResolver = semanticsResolver;
            _directoryOwnershipStore = directoryOwnershipStore;
            _logger = logger;
            _ownershipAuthorizer = ownershipAuthorizer;
        }

        public async Task<AudiobookFilesystemDeleteResult> DeleteAsync(
            Audiobook audiobook,
            bool deleteFolder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new AudiobookFilesystemDeleteResult();
            var trackedFilePaths = CollectTrackedFilePaths(audiobook);
            var boundaryPath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
                ? audiobook.BasePath
                : !string.IsNullOrWhiteSpace(audiobook.FilePath)
                    ? audiobook.FilePath
                    : trackedFilePaths.FirstOrDefault();
            var semantics = await ResolveDeleteSemanticsAsync(
                boundaryPath,
                result,
                cancellationToken);
            if (semantics == null)
            {
                return result;
            }

            var deleteSemantics = semantics.Value;
            var deleteTarget = await ResolveDeleteFolderTargetAsync(
                audiobook,
                trackedFilePaths,
                deleteSemantics,
                result,
                cancellationToken);

            if (deleteTarget != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutationToken = CancellationToken.None;
                var targetAuthorization = await AuthorizeDeleteTargetAsync(
                    deleteTarget,
                    result,
                    cancellationToken);
                if (targetAuthorization == null)
                {
                    return result;
                }

                bool contentsDeleted;
                using (targetAuthorization)
                {
                    contentsDeleted = TryDeleteFolderContents(
                        deleteTarget,
                        targetAuthorization,
                        result);
                }

                if (deleteFolder && contentsDeleted)
                {
                    await TryDeleteAudiobookFolderAsync(
                        audiobook,
                        deleteTarget,
                        result,
                        mutationToken);
                }
            }
            else
            {
                var protectedRoots = await GetProtectedRootPathsAsync(
                    cancellationToken);
                var fallbackFolderRoot = ResolveAudiobookFolderPath(audiobook, trackedFilePaths, deleteSemantics);
                var allowedRoots = protectedRoots
                    .Concat(string.IsNullOrWhiteSpace(fallbackFolderRoot) ? [] : [fallbackFolderRoot])
                    .ToList();
                cancellationToken.ThrowIfCancellationRequested();
                var mutationToken = CancellationToken.None;
                foreach (var trackedFilePath in trackedFilePaths)
                {
                    TryDeleteFile(trackedFilePath, result, allowedRoots);
                }

                if (deleteFolder)
                {
                    await RecoverMissingOwnedDirectoryAsync(
                        fallbackFolderRoot,
                        deleteSemantics,
                        "audiobook",
                        mutationToken);
                    await RecoverMissingOwnedAuthorParentAsync(
                        audiobook,
                        fallbackFolderRoot,
                        deleteSemantics,
                        mutationToken);
                }
            }

            return result;
        }

        private sealed class DeleteFolderTarget
        {
            public required string FolderPath { get; init; }
            public required IReadOnlyCollection<string> ProtectedRoots { get; init; }
            public required IReadOnlyCollection<string> AllowedMutationRoots { get; init; }
            public required FileSystemPathSemantics Semantics { get; init; }
            public required IReadOnlyList<LibraryDirectoryOwnership> OwnedDirectories { get; init; }
        }

        private async Task<FileSystemPathSemantics?> ResolveDeleteSemanticsAsync(
            string? boundaryPath,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(boundaryPath))
            {
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSystemPathSemantics? bestSemantics = null;
                var bestRootLength = -1;
                foreach (var root in await _rootFolderService.GetAllAsync())
                {
                    if (string.IsNullOrWhiteSpace(root.Path))
                    {
                        continue;
                    }

                    var rootResolution = await _semanticsResolver.ResolveAsync(
                        root.Path,
                        root.CaseSensitivityMode,
                        cancellationToken);
                    if (rootResolution.State != PathIdentityState.Valid
                        || !FileSystemPathIdentity.IsSameOrInside(
                            boundaryPath,
                            root.Path,
                            rootResolution.Semantics))
                    {
                        continue;
                    }

                    var canonicalRoot = FileSystemPathIdentity.Canonicalize(
                        root.Path,
                        rootResolution.Semantics.Syntax);
                    if (canonicalRoot.Length > bestRootLength)
                    {
                        bestSemantics = rootResolution.Semantics;
                        bestRootLength = canonicalRoot.Length;
                    }
                }

                if (bestSemantics.HasValue)
                {
                    return bestSemantics.Value;
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(exception, "Failed to resolve root folder semantics while deleting audiobook files");
            }

            var resolution = await _semanticsResolver.ResolveAsync(
                boundaryPath,
                cancellationToken: cancellationToken);
            if (resolution.State == PathIdentityState.Valid)
            {
                return resolution.Semantics;
            }

            result.Warnings.Add(
                "Filesystem case sensitivity could not be resolved, so deletion was blocked.");
            return null;
        }

        private static IReadOnlyList<string> CollectTrackedFilePaths(Audiobook audiobook)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var normalizedLegacy = NormalizePath(audiobook.FilePath);
                if (!string.IsNullOrWhiteSpace(normalizedLegacy))
                {
                    paths.Add(normalizedLegacy);
                }
            }

            if (audiobook.Files != null)
            {
                foreach (var normalizedTracked in audiobook.Files
                    .Select(file => NormalizePath(file.Path))
                    .Where(normalizedTracked => !string.IsNullOrWhiteSpace(normalizedTracked)))
                {
                    paths.Add(normalizedTracked!);
                }
            }

            return paths.ToList();
        }

        private void TryDeleteFile(
            string path,
            AudiobookFilesystemDeleteResult result,
            IEnumerable<string> allowedRoots)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (!FileSystemSafety.TryDeleteFile(
                    path,
                    allowedRoots,
                    out var reason))
            {
                var warning = $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(
                    "Blocked audiobook file delete for {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(path),
                    LogRedaction.SanitizeText(reason));
                return;
            }

            result.DeletedFiles++;
            _logger.LogInformation(
                "Deleted audiobook file {Path}",
                LogRedaction.SanitizeFilePath(path));
        }

        private bool TryDeleteFolderContents(
            DeleteFolderTarget deleteTarget,
            PinnedDirectoryCreation.PinnedDirectoryAnchor targetAuthorization,
            AudiobookFilesystemDeleteResult result)
        {
            var folderPath = deleteTarget.FolderPath;
            if (!Directory.Exists(folderPath))
            {
                return true;
            }

            if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                folderPath,
                out var files,
                out var directories,
                out var reason))
            {
                result.Warnings.Add(
                    "Refused to recursively delete the audiobook folder because it contains a symbolic link or reparse point.");
                _logger.LogWarning(
                    "Blocked recursive audiobook delete for {FolderPath}: {Reason}",
                    LogRedaction.SanitizeFilePath(folderPath),
                    LogRedaction.SanitizeText(reason));
                return false;
            }

            var ownershipMarkerPaths = deleteTarget.OwnedDirectories
                .SelectMany(LibraryDirectoryOwnershipMarker.GetMarkerPaths)
                .ToHashSet(deleteTarget.Semantics.Comparer);
            foreach (var filePath in files)
            {
                if (!targetAuthorization.VisiblePathMatches())
                {
                    result.Warnings.Add(
                        "Refused to continue deleting audiobook contents because the authorized folder changed.");
                    return false;
                }
                if (!ownershipMarkerPaths.Contains(filePath))
                {
                    TryDeleteFile(filePath, result, deleteTarget.AllowedMutationRoots);
                }
            }

            foreach (var directoryPath in directories
                .Where(path => !deleteTarget.OwnedDirectories.Any(ownership =>
                    FileSystemPathIdentity.AreEquivalent(
                        ownership.CanonicalPath,
                        path,
                        deleteTarget.Semantics)))
                .OrderByDescending(path => path.Length))
            {
                if (!targetAuthorization.VisiblePathMatches())
                {
                    result.Warnings.Add(
                        "Refused to continue deleting audiobook contents because the authorized folder changed.");
                    return false;
                }
                if (!FileSystemSafety.TryDeleteEmptyDirectory(
                        directoryPath,
                        deleteTarget.AllowedMutationRoots,
                        out var directoryReason))
                {
                    _logger.LogDebug(
                        "Skipped nested audiobook directory delete for {FolderPath}: {Reason}",
                        LogRedaction.SanitizeFilePath(directoryPath),
                        LogRedaction.SanitizeText(directoryReason));
                }
            }

            return true;
        }
    }
}
