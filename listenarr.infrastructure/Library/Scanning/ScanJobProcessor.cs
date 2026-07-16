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
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class ScanJobProcessor : IScanJobProcessor
    {
        private async Task ProcessJobCoreAsync(
            ScanJob job,
            Action<Func<CancellationToken, Task>> registerPostCompletionEffects,
            CancellationToken stoppingToken)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobId"] = job.Id,
                ["AudiobookId"] = job.AudiobookId,
                ["CorrelationId"] = job.CorrelationId ?? job.Id.ToString("N")
            });
            _metrics.Increment("worker.scan.job.started");
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation("Processing scan job {JobId} for audiobook {AudiobookId}", job.Id, job.AudiobookId);
                try
                {
                    await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new { jobId = job.Id.ToString(), audiobookId = job.AudiobookId, status = "Processing", startedAt = DateTime.UtcNow });
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
                try { _queue.UpdateJobStatus(job.Id, "Processing"); }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
                using var scope = _scopeFactory.CreateScope();
                var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                var audiobook = await audiobookRepository.GetByIdAsync(job.AudiobookId);
                if (audiobook == null)
                {
                    _logger.LogWarning("Audiobook {Id} not found for scan job {JobId}", job.AudiobookId, job.Id);
                    await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook: null,
                        "Audiobook not found",
                        stoppingToken);
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                var scanRoot = job.Path;
                var usedBasePath = false;
                var moveOwned = job.MoveScanHandoffId.HasValue;
                if (moveOwned)
                {
                    if (string.IsNullOrWhiteSpace(job.Path)
                        || !job.PathIdentity.HasValue)
                    {
                        await RecordMoveScanFailureAsync(
                            historyRepository,
                            job,
                            audiobook,
                            "The move scan handoff has no authoritative target filesystem identity.",
                            stoppingToken);
                        _metrics.Increment("worker.scan.job.skipped");
                        return;
                    }

                    PathIdentitySnapshot targetIdentity;
                    try
                    {
                        targetIdentity = await ValidateScanIdentityAsync(
                            job.Path,
                            job.PathIdentity.Value,
                            stoppingToken);
                    }
                    catch (InvalidOperationException exception)
                    {
                        await RecordMoveScanFailureAsync(
                            historyRepository,
                            job,
                            audiobook,
                            exception.Message,
                            stoppingToken);
                        _metrics.Increment("worker.scan.job.skipped");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                        || !FileSystemPathIdentity.AreEquivalent(
                            audiobook.BasePath,
                            job.Path,
                            targetIdentity.Semantics))
                    {
                        var superseded = await RecordMoveScanSupersededAsync(
                            job,
                            "A newer audiobook destination superseded this move scan handoff.",
                            stoppingToken);
                        ApplyTerminalStatus(job, superseded);
                        _metrics.Increment("worker.scan.job.skipped");
                        return;
                    }

                    scanRoot = job.Path;
                }
                else if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    scanRoot = audiobook.BasePath;
                    usedBasePath = true;
                    _logger.LogDebug("Using audiobook BasePath as scan root for job {JobId}: {ScanRoot}", job.Id, scanRoot);
                }
                else if (string.IsNullOrEmpty(scanRoot))
                {
                    try
                    {
                        var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                        var settings = await configService.GetApplicationSettingsAsync();
                        scanRoot = settings.OutputPath;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to read settings for scan job {JobId}", job.Id);
                    }
                }

                if (usedBasePath && (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot)))
                {
                    // Do not remove tracked files or clear BasePath just because a scan cannot
                    // currently access the saved directory. Directory.Exists also returns false
                    // for permission, process-user, stale metadata, and mount visibility issues,
                    // so destructive reconciliation here can erase valid metadata after a typo
                    // or temporary access failure. Surface the scan failure and let an explicit
                    // repair/update path operation change metadata intentionally.
                    _logger.LogWarning(
                        "Audiobook BasePath is unavailable for scan job {JobId}: {Path}. Leaving tracked files unchanged.",
                        job.Id,
                        LogRedaction.SanitizeFilePath(scanRoot));
                    var failureDecision = await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook,
                        "BasePath unavailable",
                        stoppingToken);
                    registerPostCompletionEffects(token => BroadcastFailedScanAsync(
                        job,
                        failureDecision.Status,
                        failureDecision.Error,
                        token));
                    _metrics.Increment("worker.scan.job.failed");
                    return;
                }

                if (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot))
                {
                    _logger.LogWarning("Scan path not found for job {JobId}: {Path}", job.Id, LogRedaction.SanitizeFilePath(scanRoot));
                    await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook,
                        "Scan path not found",
                        stoppingToken);
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                if (!await ValidateScanRootSafetyAsync(
                        scanRoot,
                        job,
                        audiobook,
                        historyRepository,
                        stoppingToken))
                {
                    return;
                }

                FileSystemPathSemantics semantics;
                try
                {
                    if (job.PathIdentity.HasValue
                        && !string.IsNullOrWhiteSpace(job.Path)
                        && FileSystemPathIdentity.AreEquivalent(
                            scanRoot,
                            job.Path,
                            job.PathIdentity.Value.Semantics))
                    {
                        semantics = (await ValidateScanIdentityAsync(
                            scanRoot,
                            job.PathIdentity.Value,
                            stoppingToken)).Semantics;
                    }
                    else
                    {
                        var semanticsResolution = await _semanticsResolver.ResolveAsync(
                            scanRoot,
                            cancellationToken: stoppingToken);
                        if (semanticsResolution.State != PathIdentityState.Valid)
                        {
                            throw new InvalidOperationException(
                                semanticsResolution.Reason ?? "Filesystem identity unavailable");
                        }

                        semantics = semanticsResolution.Semantics;
                    }
                }
                catch (InvalidOperationException exception)
                {
                    _logger.LogWarning(
                        "Scan job {JobId} blocked because filesystem identity is unavailable: {Reason}",
                        job.Id,
                        exception.Message);
                    await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook,
                        exception.Message,
                        stoppingToken);
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                var foundFiles = ScanFileDiscovery.FindMatchingAudioFiles(
                    scanRoot,
                    audiobook,
                    job.Id,
                    _logger,
                    semantics);

                var basePath = ScanPathPlanner.CalculateBasePath(foundFiles, semantics);
                if (!string.IsNullOrEmpty(basePath))
                {
                    var basePathChanged = string.IsNullOrWhiteSpace(audiobook.BasePath)
                        || !FileSystemPathIdentity.AreEquivalent(
                            audiobook.BasePath,
                            basePath,
                            semantics);
                    audiobook.BasePath = basePath;
                    _logger.LogInformation("Set base path for audiobook '{Title}' (ID: {AudiobookId}): {BasePath}", LogRedaction.SanitizeText(audiobook.Title), audiobook.Id, LogRedaction.SanitizeFilePath(basePath));

                    // That service resolves the audiobook in a separate scope/db context and
                    // uses BasePath for containment checks, so delayed SaveChanges can cause
                    // legitimate sibling parts to be rejected during multifile scans.
                    if (basePathChanged)
                    {
                        await audiobookRepository.UpdateAsync(audiobook);
                    }
                }

                var createdFiles = 0;
                foreach (var filePath in foundFiles)
                {
                    try
                    {
                        using var afScope = _scopeFactory.CreateScope();
                        var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();

                        var created = await audioFileService.EnsureAudiobookFileAsync(audiobook, filePath, "scan");
                        if (created) createdFiles++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to add file {File} during scan job {JobId}", filePath, job.Id);
                    }
                }

                try
                {
                    var existingFiles = await fileRepository.GetByAudiobookIdAsync(audiobook.Id);

                    var foundSet = new HashSet<string>(foundFiles, semantics.Comparer);

                    var toRemove = new List<AudiobookFile>();
                    foreach (var existingFile in existingFiles
                        .Where(existingFile => !string.IsNullOrEmpty(existingFile.Path))
                        .Where(existingFile => FileUtils.IsAudioFile(existingFile.Path!)))
                    {
                        var fullPath = existingFile.Path!;
                        if (!Path.IsPathRooted(fullPath) && !string.IsNullOrEmpty(basePath))
                        {
                            fullPath = Path.GetFullPath(Path.Join(basePath, fullPath));
                        }

                        if (!foundSet.Contains(fullPath))
                        {
                            toRemove.Add(existingFile);
                        }
                    }

                    List<object> removedFilesDto = new();
                    if (toRemove.Count > 0)
                    {
                        foreach (var rem in toRemove)
                        {
                            try
                            {
                                removedFilesDto.Add(new { id = rem.Id, path = rem.Path });
                                await fileRepository.DeleteAsync(rem.Id);
                                _logger.LogInformation("Removing missing AudiobookFile DB row Id={Id} Path={Path}", rem.Id, LogRedaction.SanitizeFilePath(rem.Path));

                                var historyEntry = new History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title ?? "Unknown",
                                    EventType = "File Removed",
                                    Message = $"File removed (no longer exists): {Path.GetFileName(rem.Path)}",
                                    Source = "Scan",
                                    Data = JsonSerializer.Serialize(new
                                    {
                                        FilePath = rem.Path,
                                        FileSize = rem.Size,
                                        Format = rem.Format,
                                        Source = rem.Source
                                    }),
                                    Timestamp = DateTime.UtcNow
                                };
                                await historyRepository.AddAsync(historyEntry);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to remove AudiobookFile Id={Id} Path={Path}", rem.Id, LogRedaction.SanitizeFilePath(rem.Path));
                            }
                        }

                        // Broadcast only after the coordinated filesystem/database work releases
                        // the per-audiobook lock.
                        registerPostCompletionEffects(token => BroadcastFilesRemovedAsync(
                            audiobook.Id,
                            removedFilesDto,
                            token));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to reconcile audiobook files after scan job {JobId}", job.Id);
                }

                try
                {
                    var needsUpdate = false;
                    if (!string.IsNullOrEmpty(audiobook.FilePath))
                    {
                        if (System.IO.File.Exists(audiobook.FilePath))
                        {
                            try
                            {
                                using var afScope = _scopeFactory.CreateScope();
                                var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                                var created = await audioFileService.EnsureAudiobookFileAsync(audiobook, audiobook.FilePath, "scan-legacy");
                                if (created)
                                {
                                    _logger.LogInformation("Migrated legacy filePath to AudiobookFile record for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));
                                    createdFiles++;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to migrate legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));
                            }
                        }
                        else
                        {
                            audiobook.FilePath = null;
                            audiobook.FileSize = null;
                            needsUpdate = true;
                            _logger.LogInformation("Cleared missing legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(audiobook.FilePath));

                            var historyEntry = new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "File Removed",
                                Message = $"Legacy file path cleared (file no longer exists)",
                                Source = "Scan",
                                Data = JsonSerializer.Serialize(new
                                {
                                    FilePath = audiobook.FilePath,
                                    Source = "legacy-migration"
                                }),
                                Timestamp = DateTime.UtcNow
                            };
                            await historyRepository.AddAsync(historyEntry);
                        }
                    }

                    if (needsUpdate)
                    {
                        await audiobookRepository.UpdateAsync(audiobook);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to handle legacy filePath migration for audiobook {AudiobookId}", audiobook.Id);
                }

                var updated = await audiobookRepository.GetByIdAsync(audiobook.Id);
                if (updated == null)
                {
                    const string error = "Audiobook disappeared before scan completion";
                    _logger.LogWarning(
                        "Audiobook {AudiobookId} disappeared before scan job {JobId} could complete",
                        audiobook.Id,
                        job.Id);
                    await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook,
                        error,
                        stoppingToken);
                    _metrics.Increment("worker.scan.job.failed");
                    return;
                }

                var terminalDecision = await RecordScanCompletionAsync(
                    historyRepository,
                    job,
                    audiobook,
                    foundFiles.Count,
                    createdFiles,
                    scanRoot,
                    stoppingToken);
                ApplyTerminalStatus(job, terminalDecision);
                if (!string.Equals(terminalDecision.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                registerPostCompletionEffects(token => RunSuccessfulPostCompletionEffectsAsync(
                    job,
                    audiobook,
                    foundFiles.Count,
                    createdFiles,
                    token));

                _metrics.Increment("worker.scan.job.completed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                await HandleUnexpectedScanFailureAsync(
                    job,
                    ex,
                    registerPostCompletionEffects,
                    stoppingToken);
            }
        }

    }
}
