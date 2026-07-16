/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;
namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService
    {
        private async Task AddHistoryAsync(Audiobook audiobook, RenameResult result)
        {
            if (_historyRepository == null) return;
            try
            {
                var fileCount = result.RenamedFiles.Count(f => f.Success);
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) parts.Add("folder organized");
                if (fileCount > 0) parts.Add($"{fileCount} file(s) renamed");
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title,
                    EventType = "Organized",
                    Message = parts.Count == 0 ? "Files organized" : string.Join(", ", parts),
                    Source = "Organize",
                    Timestamp = DateTime.UtcNow
                }, default);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write organize history for audiobook {AudiobookId}", audiobook.Id);
            }
        }

        private void UpdateAudiobookPathSummary(
            Audiobook audiobook,
            string? requestedBasePath,
            FileSystemPathSemantics semantics)
        {
            var resolvedFiles = new List<(AudiobookFile? File, string Path)>();
            foreach (var file in audiobook.Files ?? [])
            {
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    continue;
                }

                var resolvedPath = ResolveStoredFilePath(
                    audiobook,
                    file.Path,
                    semantics,
                    "Tracked audiobook file path is missing or invalid.",
                    out var error);
                if (error != null)
                {
                    throw new InvalidOperationException(error);
                }

                resolvedFiles.Add((file, resolvedPath));
            }

            if (resolvedFiles.Count == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var resolvedPath = ResolveStoredFilePath(
                    audiobook,
                    audiobook.FilePath,
                    semantics,
                    "Legacy audiobook file path is missing or invalid.",
                    out var error);
                if (error != null)
                {
                    throw new InvalidOperationException(error);
                }

                resolvedFiles.Add((null, resolvedPath));
            }

            audiobook.BasePath = !string.IsNullOrWhiteSpace(requestedBasePath)
                ? NormalizePath(requestedBasePath)
                : ComputeCommonBasePath(resolvedFiles.Select(file => file.Path), semantics);
            if (resolvedFiles.Count == 0)
            {
                return;
            }

            var primary = resolvedFiles
                .OrderBy(file => file.Path, semantics.Comparer)
                .First();
            audiobook.FilePath = primary.Path;
            if (primary.File?.Size > 0)
            {
                audiobook.FileSize = primary.File.Size;
            }
        }

        private static List<PreviewFileEntry> GetFileEntries(
            Audiobook audiobook,
            FileSystemPathSemantics semantics)
        {
            var resolvedFiles = new List<(int FileId, string Path)>();
            if (audiobook.Files != null && audiobook.Files.Count > 0)
            {
                foreach (var file in audiobook.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)))
                {
                    var resolvedPath = ResolveStoredFilePath(
                        audiobook,
                        file.Path,
                        semantics,
                        "Tracked audiobook file path is missing or invalid.",
                        out var error);
                    if (error != null)
                    {
                        throw new InvalidOperationException(error);
                    }

                    resolvedFiles.Add((file.Id, resolvedPath));
                }
            }
            else if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var resolvedPath = ResolveStoredFilePath(
                    audiobook,
                    audiobook.FilePath,
                    semantics,
                    "Legacy audiobook file path is missing or invalid.",
                    out var error);
                if (error != null)
                {
                    throw new InvalidOperationException(error);
                }

                resolvedFiles.Add((0, resolvedPath));
            }

            return resolvedFiles
                .OrderBy(file => file.Path, semantics.Comparer)
                .Select((file, index) => new PreviewFileEntry(
                    file.FileId,
                    file.Path,
                    Path.GetExtension(file.Path) ?? ".m4b",
                    index + 1))
                .ToList();
        }

        private string BuildExpectedPath(Audiobook audiobook, PreviewFileEntry file, ApplicationSettings settings, string basePath, bool isCustomBasePath, bool isMultiFile)
        {
            var folderPattern = settings.FolderNamingPattern;
            var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
            var variables = BuildNamingVariables(audiobook, folderPattern, filePattern, file.SequenceNumber, isMultiFile);
            var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
                && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0 || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

            string relativePath;
            if (string.IsNullOrWhiteSpace(folderPattern))
            {
                var legacyPattern = string.IsNullOrWhiteSpace(filePattern) ? "{Author}/{Title}/{Title}" : filePattern;
                relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, false);
            }
            else if (isCustomBasePath)
            {
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, !PatternAllowsSubfolders(effectiveFilePattern));
            }
            else
            {
                var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
                var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, false);
                var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, !PatternAllowsSubfolders(effectiveFilePattern));
                if (isMultiFile && !patternHasNumberTokens) fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, file.SequenceNumber);
                relativePath = string.IsNullOrWhiteSpace(folderRelative) ? fileRelative : CombineWithOptionalBase(folderRelative, fileRelative);
            }

            if ((string.IsNullOrWhiteSpace(folderPattern) || isCustomBasePath) && isMultiFile && !patternHasNumberTokens)
                relativePath = FileUtils.AppendSequenceSuffix(relativePath, file.SequenceNumber);
            if (!relativePath.EndsWith(file.Extension, StringComparison.OrdinalIgnoreCase)) relativePath += file.Extension;

            return string.IsNullOrWhiteSpace(basePath) ? NormalizePath(relativePath) : NormalizePath(CombineWithOptionalBase(basePath, relativePath));
        }

        private static Dictionary<string, object> BuildNamingVariables(Audiobook audiobook, string? folderPattern, string? filePattern, int sequenceNumber, bool isMultiFile)
        {
            var usesSubtitleToken = (!string.IsNullOrWhiteSpace(folderPattern) && folderPattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(filePattern) && filePattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0);
            var combinedTitle = !usesSubtitleToken
                && !string.IsNullOrWhiteSpace(audiobook.Subtitle)
                && !string.IsNullOrWhiteSpace(audiobook.Title)
                && !audiobook.Title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase)
                ? $"{audiobook.Title}: {audiobook.Subtitle}"
                : audiobook.Title;
            var narrator = audiobook.Narrators != null ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty;

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", audiobook.Authors?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown Author" },
                { "Series", audiobook.Series ?? string.Empty },
                { "Title", string.IsNullOrWhiteSpace(combinedTitle) ? "Unknown Title" : combinedTitle },
                { "Subtitle", audiobook.Subtitle ?? string.Empty },
                { "Edition", audiobook.Edition ?? string.Empty },
                { "Narrator", narrator },
                { "Publisher", audiobook.Publisher ?? string.Empty },
                { "Language", audiobook.Language ?? string.Empty },
                { "Asin", audiobook.Asin ?? string.Empty },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", audiobook.Quality ?? string.Empty },
                { "DiskNumber", isMultiFile ? sequenceNumber : string.Empty },
                { "ChapterNumber", isMultiFile ? sequenceNumber : string.Empty }
            };
        }

        private async Task<List<RootFolder>> LoadRootFoldersAsync()
        {
            if (_rootFolderService == null) return new();
            try { return await _rootFolderService.GetAllAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load root folders for organize preview; falling back to application output path");
                return new();
            }
        }

        private static (string BasePath, bool IsCustomBasePath) ResolveNamingBasePath(
            string? currentBasePath,
            ApplicationSettings settings,
            List<RootFolder> rootFolders,
            FileSystemPathSemantics semantics)
        {
            if (string.IsNullOrWhiteSpace(currentBasePath))
            {
                var defaultRoot = rootFolders.FirstOrDefault(r => r.IsDefault)?.Path;
                return (NormalizePath(!string.IsNullOrWhiteSpace(defaultRoot) ? defaultRoot : settings.OutputPath), false);
            }

            var normalizedCurrent = NormalizePath(currentBasePath);
            var matchingRoot = rootFolders.Where(r => IsSamePathOrWithin(normalizedCurrent, NormalizePath(r.Path), semantics))
                .OrderByDescending(r => NormalizePath(r.Path).Length).FirstOrDefault();
            if (matchingRoot != null) return (NormalizePath(matchingRoot.Path), false);
            if (!string.IsNullOrWhiteSpace(settings.OutputPath) && IsSamePathOrWithin(normalizedCurrent, NormalizePath(settings.OutputPath), semantics))
                return (NormalizePath(settings.OutputPath), false);
            return (normalizedCurrent, true);
        }

        private static IReadOnlyCollection<string> BuildAllowedRoots(
            ApplicationSettings settings,
            List<RootFolder> rootFolders,
            string currentBasePath,
            FileSystemPathSemantics semantics)
        {
            var configuredRoots = new HashSet<string>(semantics.Comparer);
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                configuredRoots.Add(NormalizePath(settings.OutputPath));
            }

            foreach (var root in rootFolders.Where(root => !string.IsNullOrWhiteSpace(root.Path)))
            {
                configuredRoots.Add(NormalizePath(root.Path));
            }

            var authoritativeRoot = configuredRoots
                .Where(root => IsSamePathOrWithin(currentBasePath, root, semantics))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(authoritativeRoot)
                ? [NormalizePath(currentBasePath)]
                : [authoritativeRoot];
        }

        private static bool IsPathWithinAllowedRoots(
            string path,
            IReadOnlyCollection<string> allowedRoots,
            FileSystemPathSemantics semantics)
            => !string.IsNullOrWhiteSpace(path) && allowedRoots.Any(root => IsSamePathOrWithin(path, root, semantics));

        private string ComputeCurrentBasePath(Audiobook audiobook, FileSystemPathSemantics semantics)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) return NormalizePath(audiobook.BasePath);
            var filePaths = audiobook.Files?.Where(f => !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path!).ToList() ?? new();
            if (filePaths.Count == 0 && !string.IsNullOrWhiteSpace(audiobook.FilePath)) filePaths.Add(audiobook.FilePath);
            return ComputeCommonBasePath(filePaths, semantics);
        }

        private static string ComputeCurrentBasePathSeed(Audiobook audiobook)
        {
            if (!string.IsNullOrWhiteSpace(audiobook.BasePath)) return NormalizePath(audiobook.BasePath);
            var firstPath = audiobook.Files?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Path))?.Path;
            if (string.IsNullOrWhiteSpace(firstPath)) firstPath = audiobook.FilePath;
            return string.IsNullOrWhiteSpace(firstPath)
                ? string.Empty
                : NormalizePath(Path.GetDirectoryName(firstPath) ?? firstPath);
        }

        private string ComputeCommonBasePath(
            IEnumerable<string> paths,
            FileSystemPathSemantics semantics)
        {
            var normalized = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath).ToList();
            if (normalized.Count == 0) return string.Empty;
            if (normalized.Count == 1)
            {
                var single = normalized[0];
                return _fileSystem.DirectoryExists(single) ? single : NormalizePath(Path.GetDirectoryName(single) ?? single);
            }

            var common = GetCommonDirectory(normalized, semantics);
            return string.IsNullOrWhiteSpace(common) ? string.Empty : NormalizePath(common);
        }

        private static string GetCommonDirectory(
            IReadOnlyCollection<string> paths,
            FileSystemPathSemantics semantics)
        {
            if (paths.Count == 0) return string.Empty;
            var common = FileSystemPathIdentity.Canonicalize(paths.First(), semantics.Syntax);
            foreach (var path in paths.Skip(1))
            {
                var candidate = FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
                while (!FileSystemPathIdentity.IsSameOrInside(candidate, common, semantics))
                {
                    var parent = NormalizePath(Path.GetDirectoryName(common));
                    if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, common, semantics))
                    {
                        return string.Empty;
                    }

                    common = parent;
                }
            }

            return common;
        }

        private static string CombineWithOptionalBase(string basePath, string relativePath)
        {
            var safeRelative = relativePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(basePath)) return safeRelative;
            if (Path.IsPathRooted(safeRelative)) return safeRelative;
            return Path.Join(basePath, safeRelative);
        }

        private static string CombineRelativePath(string basePath, string relativePath)
        {
            var safeRelative = relativePath ?? string.Empty;
            if (Path.IsPathRooted(safeRelative))
            {
                var root = Path.GetPathRoot(safeRelative);
                if (!string.IsNullOrWhiteSpace(root) && safeRelative.Length >= root.Length)
                {
                    safeRelative = safeRelative[root.Length..];
                }
            }

            safeRelative = safeRelative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(safeRelative))
            {
                var root = Path.GetPathRoot(safeRelative);
                if (!string.IsNullOrWhiteSpace(root) && safeRelative.Length >= root.Length)
                {
                    safeRelative = safeRelative[root.Length..];
                }

                safeRelative = safeRelative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return NormalizePath(Path.Join(basePath, safeRelative));
        }

        private static string NormalizePath(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : FileUtils.NormalizeStoredPath(path);

        private static bool PathsEqual(
            string? left,
            string? right,
            FileSystemPathSemantics semantics)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && FileSystemPathIdentity.AreEquivalent(NormalizePath(left), NormalizePath(right), semantics);

        private static bool IsSamePathOrWithin(
            string childPath,
            string rootPath,
            FileSystemPathSemantics semantics)
            => FileSystemPathIdentity.IsSameOrInside(childPath, rootPath, semantics);

        private static bool PatternAllowsSubfolders(string pattern)
            => pattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || pattern.IndexOf('/') >= 0
                || pattern.IndexOf('\\') >= 0;

        private sealed record PreviewFileEntry(int FileId, string CurrentPath, string Extension, int SequenceNumber);
    }
}
