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
        private readonly ILogger<AudiobookFilesystemDeleteService> _logger;

        public AudiobookFilesystemDeleteService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audioFileRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            IFileSystemSemanticsResolver semanticsResolver,
            ILogger<AudiobookFilesystemDeleteService> logger)
        {
            _audiobookRepository = audiobookRepository;
            _audioFileRepository = audioFileRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _semanticsResolver = semanticsResolver;
            _logger = logger;
        }

        public async Task<AudiobookFilesystemDeleteResult> DeleteAsync(Audiobook audiobook, bool deleteFolder)
        {
            var result = new AudiobookFilesystemDeleteResult();
            var trackedFilePaths = CollectTrackedFilePaths(audiobook);
            var boundaryPath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
                ? audiobook.BasePath
                : !string.IsNullOrWhiteSpace(audiobook.FilePath)
                    ? audiobook.FilePath
                    : trackedFilePaths.FirstOrDefault();
            var semantics = await ResolveDeleteSemanticsAsync(boundaryPath, result);
            if (semantics == null)
            {
                return result;
            }

            var deleteSemantics = semantics.Value;
            var deleteTarget = await ResolveDeleteFolderTargetAsync(audiobook, trackedFilePaths, deleteSemantics, result);

            if (deleteTarget != null)
            {
                var contentsDeleted = TryDeleteFolderContents(deleteTarget, result);

                if (deleteFolder && contentsDeleted)
                {
                    await TryDeleteAudiobookFolderAsync(audiobook, deleteTarget, result);
                }
            }
            else
            {
                var protectedRoots = await GetProtectedRootPathsAsync();
                var fallbackFolderRoot = ResolveAudiobookFolderPath(audiobook, trackedFilePaths, deleteSemantics);
                var allowedRoots = protectedRoots
                    .Concat(string.IsNullOrWhiteSpace(fallbackFolderRoot) ? [] : [fallbackFolderRoot])
                    .ToList();
                foreach (var trackedFilePath in trackedFilePaths)
                {
                    TryDeleteFile(trackedFilePath, result, allowedRoots);
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
        }

        private async Task<FileSystemPathSemantics?> ResolveDeleteSemanticsAsync(
            string? boundaryPath,
            AudiobookFilesystemDeleteResult result)
        {
            if (string.IsNullOrWhiteSpace(boundaryPath))
            {
                return null;
            }

            try
            {
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
                        root.CaseSensitivityMode);
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

            var resolution = await _semanticsResolver.ResolveAsync(boundaryPath);
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

        private void TryDeleteFile(string path, AudiobookFilesystemDeleteResult result, IEnumerable<string>? allowedRoots = null)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var originalPath = path;
                if (allowedRoots != null
                    && !FileSystemSafety.TryValidateMutationTarget(path, allowedRoots, out path, out var reason))
                {
                    result.Warnings.Add("Refused to delete a file outside the allowed library roots.");
                    _logger.LogWarning(
                        "Blocked audiobook file delete for {Path}: {Reason}",
                        LogRedaction.SanitizeFilePath(originalPath),
                        LogRedaction.SanitizeText(reason));
                    return;
                }

                File.Delete(path);
                result.DeletedFiles++;
                _logger.LogInformation("Deleted audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var warning = $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(ex, "Failed to delete audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
        }

        private bool TryDeleteFolderContents(
            DeleteFolderTarget deleteTarget,
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

            foreach (var filePath in files)
            {
                TryDeleteFile(filePath, result, deleteTarget.AllowedMutationRoots);
            }

            foreach (var directoryPath in directories.OrderByDescending(path => path.Length))
            {
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
