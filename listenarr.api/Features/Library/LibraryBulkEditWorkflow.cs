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
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryBulkEditWorkflow
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IFileNamingService _fileNamingService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly LibraryMoveWorkflow _moveWorkflow;
        private readonly ILogger<LibraryBulkEditWorkflow> _logger;

        public LibraryBulkEditWorkflow(
            IImageCacheService imageCacheService,
            IHistoryRepository historyRepository,
            IServiceScopeFactory scopeFactory,
            IFileNamingService fileNamingService,
            IApplicationPathService applicationPathService,
            IFileSystem fileSystem,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            LibraryMoveWorkflow moveWorkflow,
            ILogger<LibraryBulkEditWorkflow> logger)
        {
            _imageCacheService = imageCacheService;
            _historyRepository = historyRepository;
            _scopeFactory = scopeFactory;
            _fileNamingService = fileNamingService;
            _contentRootPath = applicationPathService.ContentRootPath;
            _fileSystem = fileSystem;
            _destinationRewriteService = destinationRewriteService ?? throw new ArgumentNullException(nameof(destinationRewriteService));
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveWorkflow = moveWorkflow ?? throw new ArgumentNullException(nameof(moveWorkflow));
            _logger = logger;
        }

        public async Task<IActionResult> BulkUpdateAsync(LibraryController.BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();
            var settings = await TryLoadApplicationSettingsAsync();
            var pathChangeMode = request.PathChange?.Mode
                ?? LibraryController.BulkPathChangeMode.None;
            var metadataUpdates = new Dictionary<string, object>(
                request.Updates ?? [],
                StringComparer.OrdinalIgnoreCase);
            metadataUpdates.Remove("moveFiles");
            metadataUpdates.Remove("deleteEmptySource");
            if (pathChangeMode == LibraryController.BulkPathChangeMode.Physical)
            {
                metadataUpdates.Remove("rootFolder");
            }

            foreach (var id in request.Ids.Distinct())
            {
                var physicalPlan = pathChangeMode == LibraryController.BulkPathChangeMode.Physical
                    ? await PlanPhysicalPathChangeAsync(
                        id,
                        request.PathChange?.DestinationRootOrPath,
                        settings)
                    : PhysicalPathChangePlan.NotRequested;
                var rootRewrite = pathChangeMode switch
                {
                    LibraryController.BulkPathChangeMode.Physical =>
                        new RootFolderRewriteOutcome(false, null, null),
                    LibraryController.BulkPathChangeMode.MetadataOnly =>
                        await RewriteRootFolderIfRequestedAsync(
                            id,
                            metadataUpdates,
                            settings,
                            request.PathChange?.DestinationRootOrPath),
                    _ => await RewriteRootFolderIfRequestedAsync(
                        id,
                        metadataUpdates,
                        settings)
                };
                var outcome = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    id,
                    _ => UpdateOneAsync(
                        id,
                        metadataUpdates,
                        rootRewrite.Rewritten,
                        pathChangeMode == LibraryController.BulkPathChangeMode.Physical));
                var errors = outcome.Errors
                    .Concat(rootRewrite.Error == null ? [] : [rootRewrite.Error])
                    .Concat(physicalPlan.Error == null ? [] : [physicalPlan.Error])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var success = outcome.Success
                    && (pathChangeMode != LibraryController.BulkPathChangeMode.MetadataOnly
                        || rootRewrite.Error == null);
                Guid? moveJobId = null;
                var resolvedDestination = pathChangeMode == LibraryController.BulkPathChangeMode.MetadataOnly
                    ? rootRewrite.Destination
                    : physicalPlan.Destination;
                var pathChangeOutcome = pathChangeMode switch
                {
                    LibraryController.BulkPathChangeMode.Physical => "not-enqueued",
                    LibraryController.BulkPathChangeMode.MetadataOnly when rootRewrite.Rewritten => "metadata-updated",
                    LibraryController.BulkPathChangeMode.MetadataOnly => "failed",
                    _ => "none"
                };

                if (pathChangeMode == LibraryController.BulkPathChangeMode.Physical)
                {
                    if (physicalPlan.Error != null || string.IsNullOrWhiteSpace(physicalPlan.Destination))
                    {
                        success = false;
                    }
                    else if (outcome.Success)
                    {
                        var enqueueResult = await _moveWorkflow.EnqueueAsync(
                            id,
                            new LibraryController.MoveRequest
                            {
                                DestinationPath = physicalPlan.Destination,
                                MoveFiles = true,
                                DeleteEmptySource = request.PathChange?.DeleteEmptySource ?? true
                            });
                        if (enqueueResult is AcceptedResult
                            {
                                Value: MoveEnqueuedResponse enqueued
                            })
                        {
                            if (enqueued.JobId == Guid.Empty)
                            {
                                success = false;
                                pathChangeOutcome = "failed";
                                errors.Add("The server did not return a durable move job ID.");
                            }
                            else
                            {
                                moveJobId = enqueued.JobId;
                                resolvedDestination = enqueued.Target;
                                pathChangeOutcome = "enqueued";
                            }
                        }
                        else
                        {
                            success = false;
                            pathChangeOutcome = "failed";
                            errors.Add(GetActionResultError(enqueueResult));
                        }
                    }
                }

                results.Add(new
                {
                    id,
                    success,
                    metadataUpdated = outcome.MetadataUpdated,
                    pathChangeOutcome,
                    moveJobId,
                    resolvedDestination,
                    errors = errors.Distinct(StringComparer.Ordinal).ToList()
                });
            }

            return new OkObjectResult(new { message = "Bulk update completed", results });
        }

        private async Task<RootFolderRewriteOutcome> RewriteRootFolderIfRequestedAsync(
            int id,
            Dictionary<string, object>? updates,
            ApplicationSettings? settings,
            string? explicitRootPath = null)
        {
            object? rootObject = explicitRootPath;
            if (rootObject == null
                && (updates == null || !updates.TryGetValue("rootFolder", out rootObject)))
            {
                return new RootFolderRewriteOutcome(false, null, null);
            }

            try
            {
                var rootPath = ExtractRootPath(rootObject);
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    return new RootFolderRewriteOutcome(false, null, "Invalid rootFolder value");
                }

                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audiobook = await repository.GetByIdAsync(id);
                if (audiobook == null)
                {
                    return new RootFolderRewriteOutcome(
                        false,
                        null,
                        $"Audiobook with ID {id} not found");
                }

                var namingPattern = !string.IsNullOrWhiteSpace(settings?.FolderNamingPattern)
                    ? settings!.FolderNamingPattern
                    : settings?.FileNamingPattern ?? string.Empty;
                var newBasePath = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(
                    audiobook,
                    rootPath,
                    namingPattern,
                    _fileNamingService);
                if (!_fileSystem.TryValidateMutationTarget(
                        newBasePath,
                        [rootPath],
                        out newBasePath,
                        out var reason))
                {
                    return new RootFolderRewriteOutcome(
                        false,
                        null,
                        $"Computed audiobook path is outside the selected root folder: {reason}");
                }

                await _destinationRewriteService.RewriteDestinationAsync(
                    id,
                    newBasePath,
                    audiobook.BasePath);
                await AddBulkUpdateHistoryAsync(
                    audiobook,
                    $"Destination path rewritten to {newBasePath} via bulk update");
                return new RootFolderRewriteOutcome(true, newBasePath, null);
            }
            catch (ListenarrApplicationException ex)
            {
                return new RootFolderRewriteOutcome(false, null, ex.SafeDetail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                return new RootFolderRewriteOutcome(
                    false,
                    null,
                    $"Failed to apply root folder for audiobook {id}: {ex.Message}");
            }
        }

        private async Task<PhysicalPathChangePlan> PlanPhysicalPathChangeAsync(
            int id,
            string? destinationRootOrPath,
            ApplicationSettings? settings)
        {
            if (string.IsNullOrWhiteSpace(destinationRootOrPath))
            {
                return new PhysicalPathChangePlan(
                    null,
                    "A destination root is required for a physical path change.");
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audiobook = await repository.GetByIdAsync(id);
                if (audiobook == null)
                {
                    return new PhysicalPathChangePlan(
                        null,
                        $"Audiobook with ID {id} not found");
                }

                var destinationRoot = destinationRootOrPath;
                var namingPattern = !string.IsNullOrWhiteSpace(settings?.FolderNamingPattern)
                    ? settings!.FolderNamingPattern
                    : settings?.FileNamingPattern ?? string.Empty;
                var destination = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(
                    audiobook,
                    destinationRoot,
                    namingPattern,
                    _fileNamingService);
                if (!_fileSystem.TryValidateMutationTarget(
                        destination,
                        [destinationRoot],
                        out destination,
                        out var reason))
                {
                    return new PhysicalPathChangePlan(
                        null,
                        $"Computed audiobook path is outside the selected destination root: {reason}");
                }

                return new PhysicalPathChangePlan(destination, null);
            }
            catch (ListenarrApplicationException ex)
            {
                return new PhysicalPathChangePlan(null, ex.SafeDetail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                return new PhysicalPathChangePlan(
                    null,
                    $"Failed to plan physical move for audiobook {id}: {ex.Message}");
            }
        }

        private static string GetActionResultError(IActionResult result)
        {
            if (result is ObjectResult { Value: not null } objectResult)
            {
                try
                {
                    var element = JsonSerializer.SerializeToElement(objectResult.Value);
                    if (element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("message", out var message)
                        && !string.IsNullOrWhiteSpace(message.GetString()))
                    {
                        return message.GetString()!;
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    // Fall back to the status-based message below.
                }

                return $"Physical move enqueue failed with status {objectResult.StatusCode ?? 500}.";
            }

            return "Physical move enqueue failed.";
        }

        private async Task<BulkUpdateOutcome> UpdateOneAsync(
            int id,
            Dictionary<string, object>? updates,
            bool rootFolderRewritten,
            bool physicalPathChangeRequested)
        {
            var errors = new List<string>();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audiobook = await repository.GetByIdAsync(id);
                if (audiobook == null)
                {
                    errors.Add($"Audiobook with ID {id} not found");
                    return new BulkUpdateOutcome(false, false, errors);
                }

                var changed = false;
                if (updates != null && updates.TryGetValue("monitored", out var monitoredObj))
                {
                    try
                    {
                        var monitored = monitoredObj is JsonElement element
                            ? element.ValueKind == JsonValueKind.True
                            : Convert.ToBoolean(monitoredObj);
                        audiobook.Monitored = monitored;
                        changed = true;
                        _logger.LogInformation("Set Monitored={Monitored} for audiobook id={Id}", monitored, id);
                        await AddBulkUpdateHistoryAsync(audiobook, $"Monitored set to {monitored}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        && ex is not OutOfMemoryException
                        && ex is not StackOverflowException)
                    {
                        errors.Add($"Invalid monitored value: {ex.Message}");
                    }
                }

                if (updates != null && updates.TryGetValue("qualityProfileId", out var qualityProfileObj))
                {
                    try
                    {
                        var qualityProfileId = qualityProfileObj is JsonElement element
                            ? element.GetInt32()
                            : Convert.ToInt32(qualityProfileObj);
                        audiobook.QualityProfileId = qualityProfileId;
                        changed = true;
                        _logger.LogInformation(
                            "Set QualityProfileId={Profile} for audiobook id={Id}",
                            qualityProfileId,
                            id);
                        await AddBulkUpdateHistoryAsync(
                            audiobook,
                            $"Quality profile set to {qualityProfileId}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        && ex is not OutOfMemoryException
                        && ex is not StackOverflowException)
                    {
                        errors.Add($"Invalid qualityProfileId value: {ex.Message}");
                    }
                }

                if (!changed && !rootFolderRewritten && !physicalPathChangeRequested)
                {
                    errors.Add("No valid updates provided for this audiobook");
                    return new BulkUpdateOutcome(false, false, errors);
                }

                if (changed)
                {
                    await repository.UpdateAsync(audiobook);
                }

                return new BulkUpdateOutcome(errors.Count == 0, changed, errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                errors.Add($"Unhandled error: {ex.Message}");
                return new BulkUpdateOutcome(false, false, errors);
            }
        }

        private sealed record RootFolderRewriteOutcome(
            bool Rewritten,
            string? Destination,
            string? Error);
        private sealed record PhysicalPathChangePlan(string? Destination, string? Error)
        {
            public static PhysicalPathChangePlan NotRequested { get; } = new(null, null);
        }

        private sealed record BulkUpdateOutcome(
            bool Success,
            bool MetadataUpdated,
            List<string> Errors);

        private async Task<ApplicationSettings?> TryLoadApplicationSettingsAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                return await configService.GetApplicationSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while performing bulk update");
                return null;
            }
        }

        private static string? ExtractRootPath(object rootObj)
        {
            if (rootObj is JsonElement jr)
            {
                return jr.ValueKind == JsonValueKind.String ? jr.GetString() : null;
            }

            return rootObj.ToString();
        }

        private async Task AddBulkUpdateHistoryAsync(Audiobook audiobook, string message)
        {
            await _historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "Updated",
                Message = message,
                Source = "BulkUpdate",
                Timestamp = DateTime.UtcNow
            });
        }

    }
}
