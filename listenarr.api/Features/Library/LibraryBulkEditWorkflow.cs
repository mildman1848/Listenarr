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
using System.Text.RegularExpressions;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryBulkEditWorkflow
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IFileNamingService _fileNamingService;
        private readonly string _contentRootPath;
        private readonly IFileSystem _fileSystem;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
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
            _logger = logger;
        }

        public async Task<IActionResult> BulkDeleteAsync(LibraryController.BulkDeleteRequest request)
        {
            if (request.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk deletion" });
            }

            var deletedCount = 0;
            var deletedImagesCount = 0;
            var errors = new List<string>();
            var deletedIds = new List<int>();

            foreach (var id in request.Ids.Distinct())
            {
                var outcome = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    id,
                    _ => DeleteOneAsync(id));
                deletedImagesCount += outcome.DeletedImages;
                if (outcome.Deleted)
                {
                    deletedCount++;
                    deletedIds.Add(id);
                }
                if (outcome.Error != null)
                {
                    errors.Add(outcome.Error);
                }
            }

            if (deletedCount == 0 && errors.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobooks were successfully deleted", errors });
            }

            object result = errors.Any()
                ? new
                {
                    message = $"Partially successful: deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}, {errors.Count} error{(errors.Count != 1 ? "s" : "")} occurred",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds,
                    errors
                }
                : new
                {
                    message = $"Successfully deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds
                };

            return new OkObjectResult(result);
        }

        public async Task<IActionResult> BulkUpdateAsync(LibraryController.BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return new BadRequestObjectResult(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();
            var settings = await TryLoadApplicationSettingsAsync();

            foreach (var id in request.Ids.Distinct())
            {
                var rootRewrite = await RewriteRootFolderIfRequestedAsync(
                    id,
                    request.Updates,
                    settings);
                var outcome = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    id,
                    _ => UpdateOneAsync(id, request.Updates, rootRewrite.Rewritten));
                var errors = outcome.Errors
                    .Concat(rootRewrite.Error == null ? [] : [rootRewrite.Error])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                results.Add(new { id, success = outcome.Success, errors });
            }

            return new OkObjectResult(new { message = "Bulk update completed", results });
        }

        private async Task<BulkDeleteOutcome> DeleteOneAsync(int id)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audiobook = await repository.GetByIdAsync(id);
                if (audiobook == null)
                {
                    return new BulkDeleteOutcome(false, 0, $"Audiobook with ID {id} not found");
                }

                var deletedImages = await DeleteCachedImageAsync(audiobook);
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown Title",
                    EventType = "Deleted",
                    Message = $"Audiobook '{audiobook.Title}' deleted via bulk operation",
                    Source = "BulkDelete",
                    Timestamp = DateTime.UtcNow
                });

                if (!await repository.DeleteByIdAsync(id))
                {
                    return new BulkDeleteOutcome(false, deletedImages, $"Failed to delete audiobook with ID {id}");
                }

                _logger.LogInformation(
                    "Deleted audiobook '{Title}' (ID: {Id}) via bulk operation",
                    LogRedaction.SanitizeText(audiobook.Title),
                    id);
                return new BulkDeleteOutcome(true, deletedImages, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error during bulk delete for ID {Id}: {Message}", id, ex.Message);
                return new BulkDeleteOutcome(false, 0, $"Error deleting audiobook with ID {id}: {ex.Message}");
            }
        }

        private async Task<RootFolderRewriteOutcome> RewriteRootFolderIfRequestedAsync(
            int id,
            Dictionary<string, object>? updates,
            ApplicationSettings? settings)
        {
            if (updates == null || !updates.TryGetValue("rootFolder", out var rootObject))
            {
                return new RootFolderRewriteOutcome(false, null);
            }

            try
            {
                var rootPath = ExtractRootPath(rootObject);
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    return new RootFolderRewriteOutcome(false, "Invalid rootFolder value");
                }

                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audiobook = await repository.GetByIdAsync(id);
                if (audiobook == null)
                {
                    return new RootFolderRewriteOutcome(false, $"Audiobook with ID {id} not found");
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
                        $"Computed audiobook path is outside the selected root folder: {reason}");
                }

                await _destinationRewriteService.RewriteDestinationAsync(
                    id,
                    newBasePath,
                    audiobook.BasePath);
                await AddBulkUpdateHistoryAsync(
                    audiobook,
                    $"Destination path rewritten to {newBasePath} via bulk update");
                return new RootFolderRewriteOutcome(true, null);
            }
            catch (ListenarrApplicationException ex)
            {
                return new RootFolderRewriteOutcome(false, ex.SafeDetail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                return new RootFolderRewriteOutcome(
                    false,
                    $"Failed to apply root folder for audiobook {id}: {ex.Message}");
            }
        }

        private async Task<BulkUpdateOutcome> UpdateOneAsync(
            int id,
            Dictionary<string, object>? updates,
            bool rootFolderRewritten)
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
                    return new BulkUpdateOutcome(false, errors);
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

                if (!changed && !rootFolderRewritten)
                {
                    errors.Add("No valid updates provided for this audiobook");
                    return new BulkUpdateOutcome(false, errors);
                }

                if (changed)
                {
                    await repository.UpdateAsync(audiobook);
                }

                return new BulkUpdateOutcome(true, errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                errors.Add($"Unhandled error: {ex.Message}");
                return new BulkUpdateOutcome(false, errors);
            }
        }

        private sealed record BulkDeleteOutcome(bool Deleted, int DeletedImages, string? Error);
        private sealed record RootFolderRewriteOutcome(bool Rewritten, string? Error);
        private sealed record BulkUpdateOutcome(bool Success, List<string> Errors);

        private async Task<int> DeleteCachedImageAsync(Audiobook audiobook)
        {
            try
            {
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                    if (imagePath != null)
                    {
                        var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                        if (_fileSystem.FileExists(fullPath))
                        {
                            if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                            {
                                _logger.LogWarning(
                                    "Blocked cached image delete for ASIN {Asin}: {Reason}",
                                    LogRedaction.SanitizeText(audiobook.Asin),
                                    LogRedaction.SanitizeText(reason));
                                return 0;
                            }

                            _fileSystem.DeleteFile(safePath);
                            _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                            return 1;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    return await DeleteCachedImageFromUrlAsync(audiobook);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
            }

            return 0;
        }

        private async Task<int> DeleteCachedImageFromUrlAsync(Audiobook audiobook)
        {
            try
            {
                const string marker = "/config/cache/images/library/";
                var url = audiobook.ImageUrl!;
                var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return 0;
                }

                var filename = url.Substring(idx + marker.Length);
                filename = Path.GetFileName(filename);
                var identifier = Path.GetFileNameWithoutExtension(filename);

                if (string.IsNullOrEmpty(identifier) || !Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                {
                    _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                    return 0;
                }

                var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                if (!string.IsNullOrEmpty(imagePath))
                {
                    var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                    if (_fileSystem.FileExists(fullPath))
                    {
                        if (!_fileSystem.TryValidateMutationTarget(fullPath, [_contentRootPath], out var safePath, out var reason))
                        {
                            _logger.LogWarning(
                                "Blocked cached image delete for identifier {Identifier}: {Reason}",
                                LogRedaction.SanitizeText(identifier),
                                LogRedaction.SanitizeText(reason));
                            return 0;
                        }

                        _fileSystem.DeleteFile(safePath);
                        _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                        return 1;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
            }

            return 0;
        }

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

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }
    }
}
