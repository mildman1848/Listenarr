/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryBulkEditWorkflow
    {
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
                await TryAddBulkUpdateHistoryAsync(
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
                _logger.LogError(
                    ex,
                    "Failed to apply root folder for audiobook {AudiobookId}",
                    id);
                return new RootFolderRewriteOutcome(
                    false,
                    null,
                    $"Failed to apply root folder for audiobook {id}");
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
                _logger.LogError(
                    ex,
                    "Failed to plan physical move for audiobook {AudiobookId}",
                    id);
                return new PhysicalPathChangePlan(
                    null,
                    $"Failed to plan physical move for audiobook {id}");
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
                var historyMessages = new List<string>();
                if (updates != null && updates.TryGetValue("monitored", out var monitoredObj))
                {
                    try
                    {
                        var monitored = ParseBooleanUpdate(monitoredObj);
                        audiobook.Monitored = monitored;
                        changed = true;
                        _logger.LogInformation("Set Monitored={Monitored} for audiobook id={Id}", monitored, id);
                        historyMessages.Add($"Monitored set to {monitored}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        && ex is not OutOfMemoryException
                        && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(
                            ex,
                            "Rejected invalid monitored value for audiobook {AudiobookId}",
                            id);
                        errors.Add("Invalid monitored value");
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
                        historyMessages.Add(
                            $"Quality profile set to {qualityProfileId}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        && ex is not OutOfMemoryException
                        && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(
                            ex,
                            "Rejected invalid quality profile value for audiobook {AudiobookId}",
                            id);
                        errors.Add("Invalid qualityProfileId value");
                    }
                }

                if (!changed && !rootFolderRewritten && !physicalPathChangeRequested)
                {
                    errors.Add("No valid updates provided for this audiobook");
                    return new BulkUpdateOutcome(false, false, errors);
                }

                if (changed)
                {
                    if (!await repository.UpdateAsync(audiobook))
                    {
                        errors.Add(
                            "The audiobook disappeared before its metadata update could be committed");
                        return new BulkUpdateOutcome(false, false, errors);
                    }

                    foreach (var historyMessage in historyMessages)
                    {
                        await TryAddBulkUpdateHistoryAsync(
                            audiobook,
                            historyMessage);
                    }
                }

                return new BulkUpdateOutcome(errors.Count == 0, changed, errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                _logger.LogError(
                    ex,
                    "Unhandled bulk update error for audiobook {AudiobookId}",
                    id);
                errors.Add("Unhandled bulk update error");
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

        private static bool ParseBooleanUpdate(object? value)
        {
            return value switch
            {
                bool boolean => boolean,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => throw new FormatException(
                    "The update value must be a JSON boolean.")
            };
        }

        private static string? ExtractRootPath(object rootObj)
        {
            if (rootObj is JsonElement jr)
            {
                return jr.ValueKind == JsonValueKind.String ? jr.GetString() : null;
            }

            return rootObj.ToString();
        }

        private async Task TryAddBulkUpdateHistoryAsync(
            Audiobook audiobook,
            string message)
        {
            try
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
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(
                    exception,
                    "Audiobook {AudiobookId} was updated, but its bulk-update history event could not be recorded",
                    audiobook.Id);
            }
        }

    }
}
