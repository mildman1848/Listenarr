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
using AsyncKeyedLock;
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // Background hosted service to rescan files missing metadata and populate DB fields
    public class MetadataRescanService(
        ILogger<MetadataRescanService> logger,
        IMetadataRescanProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("MetadataRescanService starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(MetadataRescanService),
                initialDelay: null,
                intervalProvider: () => Interval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
            logger.LogInformation("MetadataRescanService stopping");
        }
    }

    public class MetadataRescanProcessor(
        IServiceScopeFactory scopeFactory,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        ILogger<MetadataRescanProcessor> logger) : IMetadataRescanProcessor
    {
        private readonly AsyncNonKeyedLocker _sem = new(2); // bound concurrent extractions

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var candidates = await fileRepository.GetMissingMetadataAsync(20, cancellationToken);

            if (candidates.Any())
            {
                logger.LogInformation("Found {Count} files missing metadata to rescan", candidates.Count);
            }

            var tasks = new List<Task>();
            foreach (var candidate in candidates.Select(f => new { f.Id, f.AudiobookId, f.Path }))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var releaser = await _sem.LockAsync(cancellationToken);
                    try
                    {
                        using var taskScope = scopeFactory.CreateScope();
                        var taskFileRepository = taskScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                        var taskMetadataService = taskScope.ServiceProvider.GetRequiredService<IMetadataService>();

                        var file = await taskFileRepository.GetByIdAsync(candidate.Id, cancellationToken);
                        if (file == null)
                        {
                            logger.LogDebug("Skipping metadata rescan for missing file id={Id}", candidate.Id);
                            return;
                        }

                        var observedPath = file.Path ?? string.Empty;
                        if (!FileUtils.IsAudioFile(observedPath))
                        {
                            await RemoveNonAudioFileAsync(
                                file.Id,
                                file.AudiobookId,
                                cancellationToken);
                            return;
                        }

                        logger.LogInformation(
                            "Re-extracting metadata for file id={Id} path={Path}",
                            file.Id,
                            LogRedaction.SanitizeFilePath(observedPath));

                        cancellationToken.ThrowIfCancellationRequested();
                        var meta = await taskMetadataService.ExtractFileMetadataAsync(observedPath);
                        if (meta != null)
                        {
                            await ApplyExtractedMetadataAsync(
                                file.Id,
                                file.AudiobookId,
                                observedPath,
                                meta,
                                cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogDebug("Metadata rescan cancelled for file id={Id}", candidate.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogWarning(ex, "Failed to rescan metadata for file id={Id} path={Path}", candidate.Id, LogRedaction.SanitizeFilePath(candidate.Path));
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        private async Task RemoveNonAudioFileAsync(
            int fileId,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            await audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                async token =>
                {
                    using var applyScope = scopeFactory.CreateScope();
                    var fileRepository = applyScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                    var audiobookRepository = applyScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                    var currentFile = await fileRepository.GetByIdAsync(fileId, token);
                    if (currentFile == null
                        || currentFile.AudiobookId != audiobookId
                        || FileUtils.IsAudioFile(currentFile.Path ?? string.Empty))
                    {
                        return;
                    }

                    var audiobook = await audiobookRepository.GetByIdAsync(currentFile.AudiobookId);
                    if (audiobook != null && await AreSameLibraryPathAsync(
                        audiobook,
                        currentFile.Path,
                        applyScope.ServiceProvider.GetRequiredService<IFileSystemSemanticsResolver>(),
                        applyScope.ServiceProvider.GetRequiredService<IRootFolderService>(),
                        token))
                    {
                        audiobook.FilePath = null;
                        audiobook.FileSize = null;
                        await audiobookRepository.UpdateAsync(audiobook);
                    }

                    await fileRepository.DeleteAsync(currentFile.Id, token);
                    logger.LogInformation(
                        "Removed non-audio AudiobookFile entry id={Id} path={Path}",
                        currentFile.Id,
                        LogRedaction.SanitizeFilePath(currentFile.Path));
                },
                cancellationToken);
        }

        private async Task ApplyExtractedMetadataAsync(
            int fileId,
            int audiobookId,
            string observedPath,
            AudioMetadata metadata,
            CancellationToken cancellationToken)
        {
            await audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                async token =>
                {
                    using var applyScope = scopeFactory.CreateScope();
                    var fileRepository = applyScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                    var currentFile = await fileRepository.GetByIdAsync(fileId, token);
                    if (currentFile == null || currentFile.AudiobookId != audiobookId)
                    {
                        return;
                    }

                    if (!string.Equals(currentFile.Path, observedPath, StringComparison.Ordinal))
                    {
                        logger.LogDebug(
                            "Skipped stale metadata extraction for file id={Id}; path changed from {ObservedPath} to {CurrentPath}",
                            fileId,
                            LogRedaction.SanitizeFilePath(observedPath),
                            LogRedaction.SanitizeFilePath(currentFile.Path));
                        return;
                    }

                    var fileInfo = new FileInfo(observedPath);
                    currentFile.Size = fileInfo.Exists ? fileInfo.Length : currentFile.Size;
                    currentFile.DurationSeconds = Math.Abs(metadata.Duration.TotalSeconds) > double.Epsilon
                        ? metadata.Duration.TotalSeconds
                        : currentFile.DurationSeconds;
                    currentFile.Format = !string.IsNullOrEmpty(metadata.Format)
                        ? metadata.Format
                        : currentFile.Format;
                    currentFile.Bitrate = metadata.BitRate != 0 ? metadata.BitRate : currentFile.Bitrate;
                    currentFile.SampleRate = metadata.SampleRate != 0 ? metadata.SampleRate : currentFile.SampleRate;
                    currentFile.Channels = metadata.Channels != 0 ? metadata.Channels : currentFile.Channels;

                    await fileRepository.UpdateAsync(currentFile, token);
                    logger.LogInformation("Updated metadata for file id={Id}", currentFile.Id);
                },
                cancellationToken);
        }

        private static async Task<bool> AreSameLibraryPathAsync(
            Audiobook audiobook,
            string? filePath,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderService rootFolderService,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(audiobook.FilePath) || string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var semantics = await ResolveLibrarySemanticsAsync(
                audiobook.FilePath,
                semanticsResolver,
                rootFolderService,
                cancellationToken);
            return semantics != null
                && FileSystemPathIdentity.AreEquivalent(
                    audiobook.FilePath,
                    filePath,
                    semantics.Value);
        }

        private static async Task<FileSystemPathSemantics?> ResolveLibrarySemanticsAsync(
            string path,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderService rootFolderService,
            CancellationToken cancellationToken)
        {
            foreach (var root in await rootFolderService.GetAllAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root.Path))
                {
                    continue;
                }

                var rootResolution = await semanticsResolver.ResolveAsync(
                    root.Path,
                    root.CaseSensitivityMode,
                    cancellationToken);
                if (rootResolution.State == PathIdentityState.Valid
                    && FileSystemPathIdentity.IsSameOrInside(
                        path,
                        root.Path,
                        rootResolution.Semantics))
                {
                    return rootResolution.Semantics;
                }
            }

            var resolution = await semanticsResolver.ResolveAsync(path, cancellationToken: cancellationToken);
            return resolution.State == PathIdentityState.Valid ? resolution.Semantics : null;
        }
    }
}
