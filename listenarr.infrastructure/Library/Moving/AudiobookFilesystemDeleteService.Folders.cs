/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService : IAudiobookFilesystemDeleteService
    {
        private async Task<DeleteFolderTarget?> ResolveDeleteFolderTargetAsync(
            Audiobook audiobook,
            IReadOnlyList<string> trackedFilePaths,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var protectedRoots = await GetProtectedRootPathsAsync(cancellationToken);
            var folderPath = ResolveAudiobookFolderPath(audiobook, trackedFilePaths, semantics);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                result.Warnings.Add("Audiobook folder could not be determined, so only tracked audiobook files were deleted.");
                return null;
            }

            if (protectedRoots.Any(root => PathsEqual(root, folderPath, semantics)))
            {
                var fallbackFolderPath = ResolveTrackedFolderPath(trackedFilePaths, semantics);
                if (!string.IsNullOrWhiteSpace(fallbackFolderPath)
                    && !protectedRoots.Any(root => PathsEqual(root, fallbackFolderPath, semantics))
                    && IsSamePathOrWithin(fallbackFolderPath, folderPath, semantics))
                {
                    folderPath = fallbackFolderPath;
                }
            }

            if (IsFilesystemRoot(folderPath, semantics))
            {
                result.Warnings.Add("Refused to delete all files in a filesystem root folder.");
                return null;
            }

            if (protectedRoots.Any(root => PathsEqual(root, folderPath, semantics)))
            {
                result.Warnings.Add("Refused to delete all files in a configured library root folder.");
                return null;
            }

            var ownedDirectories = await ResolveOwnedDirectoriesForDeleteAsync(
                folderPath,
                semantics,
                result,
                cancellationToken);
            if (ownedDirectories == null)
            {
                return null;
            }
            if (!Directory.Exists(folderPath)
                && !ownedDirectories.Any(ownership =>
                    FileSystemPathIdentity.AreEquivalent(
                        ownership.CanonicalPath,
                        folderPath,
                        semantics)
                    && ownership.State == LibraryDirectoryOwnershipState.Removing))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var allFiles = await _audioFileRepository.GetAllAsync();
            var otherFilePaths = allFiles
                .Where(f => f.AudiobookId != audiobook.Id && f.Path != null)
                .Select(f => f.Path!)
                .ToList();

            if (otherFilePaths
                .Select(NormalizePath)
                .Any(p => !string.IsNullOrWhiteSpace(p) && IsSamePathOrWithin(p!, folderPath, semantics)))
            {
                result.Warnings.Add("Refused to delete all files in the audiobook folder because other audiobook files are inside it.");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var allAudiobooks = await _audiobookRepository.GetAllAsync();
            var otherAudiobookPaths = allAudiobooks
                .Where(a => a.Id != audiobook.Id)
                .Select(a => new { a.Id, a.BasePath, a.FilePath })
                .ToList();

            foreach (var otherPath in otherAudiobookPaths)
            {
                var otherBasePath = NormalizePath(otherPath.BasePath);
                if (!string.IsNullOrWhiteSpace(otherBasePath)
                    && (IsSamePathOrWithin(otherBasePath, folderPath, semantics)
                        || IsSamePathOrWithin(folderPath, otherBasePath, semantics)))
                {
                    result.Warnings.Add("Refused to delete all files in the audiobook folder because another audiobook references that location.");
                    return null;
                }

                var otherFilePath = NormalizePath(otherPath.FilePath);
                if (!string.IsNullOrWhiteSpace(otherFilePath)
                    && IsSamePathOrWithin(otherFilePath, folderPath, semantics))
                {
                    result.Warnings.Add("Refused to delete all files in the audiobook folder because another audiobook file is inside it.");
                    return null;
                }
            }

            var allowedMutationRoots = protectedRoots
                .Where(root => IsSamePathOrWithin(folderPath, root, semantics))
                .ToList();
            if (allowedMutationRoots.Count == 0)
            {
                allowedMutationRoots.Add(folderPath);
            }

            return new DeleteFolderTarget
            {
                FolderPath = folderPath,
                ProtectedRoots = protectedRoots,
                AllowedMutationRoots = allowedMutationRoots,
                Semantics = semantics,
                OwnedDirectories = ownedDirectories
            };
        }

        private async Task TryDeleteAudiobookFolderAsync(
            Audiobook audiobook,
            DeleteFolderTarget deleteTarget,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleteTarget.OwnedDirectories.Count > 0
                && !await RetireOwnedHierarchyAsync(
                    deleteTarget.OwnedDirectories,
                    cancellationToken))
            {
                result.Warnings.Add(
                    "The audiobook folder gained content while its owned directory hierarchy was being deleted.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Unowned exact audiobook folders may still be removed because the user
            // explicitly requested folder deletion. Implicit parent deletion remains
            // ownership-gated below.
            if (!FileSystemSafety.TryDeleteEmptyDirectory(
                    deleteTarget.FolderPath,
                    deleteTarget.AllowedMutationRoots,
                    out var reason))
            {
                result.Warnings.Add("Failed to delete the audiobook folder.");
                _logger.LogWarning(
                    "Failed to safely delete audiobook folder {FolderPath}: {Reason}",
                    LogRedaction.SanitizeFilePath(deleteTarget.FolderPath),
                    LogRedaction.SanitizeText(reason));
                return;
            }

            result.DeletedFolder = !Directory.Exists(deleteTarget.FolderPath)
                && !File.Exists(deleteTarget.FolderPath);
            _logger.LogInformation("Deleted audiobook folder {FolderPath}", LogRedaction.SanitizeFilePath(deleteTarget.FolderPath));
            await TryDeleteEmptyAuthorFolderAsync(
                audiobook,
                deleteTarget.FolderPath,
                deleteTarget.ProtectedRoots,
                deleteTarget.Semantics,
                result,
                cancellationToken);
        }

        private async Task TryDeleteEmptyAuthorFolderAsync(
            Audiobook audiobook,
            string deletedFolderPath,
            IReadOnlyCollection<string> protectedRoots,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            var parentFolder = NormalizePath(Path.GetDirectoryName(deletedFolderPath));
            if (string.IsNullOrWhiteSpace(parentFolder)
                || IsFilesystemRoot(parentFolder, semantics)
                || protectedRoots.Any(root => PathsEqual(root, parentFolder, semantics))
                || !IsAuthorFolder(parentFolder, audiobook.Authors?.FirstOrDefault()))
            {
                return;
            }

            LibraryDirectoryOwnershipResolution parentOwnership;
            try
            {
                parentOwnership = await _directoryOwnershipStore.ResolveOwnedAsync(
                    parentFolder,
                    semantics,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to resolve durable ownership for author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                if (!Directory.Exists(parentFolder))
                {
                    throw;
                }

                result.Warnings.Add(
                    "The empty author folder was preserved because its durable ownership could not be resolved.");
                return;
            }

            if (parentOwnership.State != LibraryDirectoryOwnershipResolutionState.Owned
                || parentOwnership.Ownership == null)
            {
                if (!Directory.Exists(parentFolder)
                    && parentOwnership.State != LibraryDirectoryOwnershipResolutionState.Unowned)
                {
                    throw new InvalidOperationException(
                        parentOwnership.Reason
                            ?? "The missing author folder has conflicting or unavailable ownership state.");
                }

                return;
            }

            var ownedParent = parentOwnership.Ownership;
            try
            {
                ValidateOwnedDirectoryForDelete(ownedParent);
                if (Directory.Exists(parentFolder)
                    && Directory.EnumerateFileSystemEntries(parentFolder).Any(path =>
                        !LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownedParent)
                            .Any(markerPath => FileSystemPathIdentity.AreEquivalent(
                                markerPath,
                                path,
                                semantics))))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to validate owned author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                if (!Directory.Exists(parentFolder))
                {
                    throw;
                }

                result.Warnings.Add(
                    "The empty author folder was preserved because its ownership proof changed.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var allAudiobooks = await _audiobookRepository.GetAllAsync();
            var otherAudiobookPaths = allAudiobooks
                .Where(a => a.Id != audiobook.Id)
                .Select(a => new { a.Id, a.BasePath, a.FilePath })
                .ToList();

            foreach (var otherPath in otherAudiobookPaths)
            {
                var otherBasePath = NormalizePath(otherPath.BasePath);
                if (!string.IsNullOrWhiteSpace(otherBasePath)
                    && (IsSamePathOrWithin(otherBasePath, parentFolder, semantics)
                        || IsSamePathOrWithin(parentFolder, otherBasePath, semantics)))
                {
                    return;
                }

                var otherFilePath = NormalizePath(otherPath.FilePath);
                if (!string.IsNullOrWhiteSpace(otherFilePath)
                    && IsSamePathOrWithin(otherFilePath, parentFolder, semantics))
                {
                    return;
                }
            }

            try
            {
                result.DeletedParentFolder = await RetireOwnedDirectoryAsync(
                    ownedParent,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to retire owned author folder {FolderPath}",
                    LogRedaction.SanitizeFilePath(parentFolder));
                throw;
            }
            _logger.LogInformation("Deleted empty parent author folder {FolderPath}", LogRedaction.SanitizeFilePath(parentFolder));
        }

        private async Task<HashSet<string>> GetProtectedRootPathsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var protectedRoots = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                var roots = await _rootFolderService.GetAllAsync();
                foreach (var normalizedRoot in roots
                    .Select(root => NormalizePath(root.Path))
                    .Where(normalizedRoot => !string.IsNullOrWhiteSpace(normalizedRoot)))
                {
                    protectedRoots.Add(normalizedRoot!);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to enumerate root folders via service while deleting audiobook files");
            }

            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                var outputPath = NormalizePath(settings?.OutputPath);
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    protectedRoots.Add(outputPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while protecting root folders during delete");
            }

            return protectedRoots;
        }

        private static string? ResolveAudiobookFolderPath(
            Audiobook audiobook,
            IReadOnlyList<string> trackedFilePaths,
            FileSystemPathSemantics semantics)
        {
            var basePath = NormalizePath(audiobook.BasePath);
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                return basePath;
            }

            var legacyFilePath = NormalizePath(audiobook.FilePath);
            if (!string.IsNullOrWhiteSpace(legacyFilePath))
            {
                return NormalizePath(Path.GetDirectoryName(legacyFilePath));
            }

            return GetCommonDirectoryPath(trackedFilePaths, semantics);
        }

        private static string? ResolveTrackedFolderPath(
            IReadOnlyList<string> trackedFilePaths,
            FileSystemPathSemantics semantics)
        {
            if (trackedFilePaths.Count == 0)
            {
                return null;
            }

            if (trackedFilePaths.Count == 1)
            {
                var directFolder = NormalizePath(Path.GetDirectoryName(trackedFilePaths[0]));
                if (string.IsNullOrWhiteSpace(directFolder))
                {
                    return null;
                }

                var folderName = Path.GetFileName(directFolder);
                if (IsLikelySegmentFolder(folderName))
                {
                    var parentFolder = NormalizePath(Path.GetDirectoryName(directFolder));
                    if (!string.IsNullOrWhiteSpace(parentFolder))
                    {
                        return parentFolder;
                    }
                }

                return directFolder;
            }

            return GetCommonDirectoryPath(trackedFilePaths, semantics);
        }

        private static bool IsLikelySegmentFolder(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            return Regex.IsMatch(
                folderName.Trim(),
                @"^(disc|disk|cd|part|chapter|track)[\s._-]*\d+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string? GetCommonDirectoryPath(
            IReadOnlyList<string> filePaths,
            FileSystemPathSemantics semantics)
        {
            if (filePaths.Count == 0)
            {
                return null;
            }

            var directories = filePaths
                .Select(p => NormalizePath(Path.GetDirectoryName(p)))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .Distinct(semantics.Comparer)
                .ToList();

            if (directories.Count == 0)
            {
                return null;
            }

            var commonPath = directories[0];
            for (var i = 1; i < directories.Count; i++)
            {
                while (!IsSamePathOrWithin(directories[i], commonPath, semantics))
                {
                    var parent = NormalizePath(Path.GetDirectoryName(commonPath));
                    if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, commonPath, semantics))
                    {
                        return null;
                    }

                    commonPath = parent;
                }
            }

            return IsFilesystemRoot(commonPath, semantics) ? null : commonPath;
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return FileUtils.NormalizeStoredPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool PathsEqual(
            string? left,
            string? right,
            FileSystemPathSemantics semantics)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && FileSystemPathIdentity.AreEquivalent(left, right, semantics);
        }

        private static bool IsSamePathOrWithin(
            string path,
            string rootPath,
            FileSystemPathSemantics semantics) =>
            FileSystemPathIdentity.IsSameOrInside(path, rootPath, semantics);


    }
}
