using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private Task ExecuteWithAudiobookLocksAsync(
        IEnumerable<int> audiobookIds,
        Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookIds,
                _ => operation(),
                globalToken));
    }

    /// <summary>
    /// Order a scan for each audiobook impacted by the importation and update audiobook base path.
    /// </summary>
    /// <param name="results">List of imported files.</param>
    private async Task EnqueueFocusedScansAsync(IEnumerable<ManualImportResultDto> results)
    {
        var groupedResults = results
            .Where(result => result.Success
                && result.Audiobook != null
                && !string.IsNullOrWhiteSpace(result.DestinationPath))
            .GroupBy(result => result.Audiobook!.Id);

        foreach (var group in groupedResults)
        {
            var scanPath = ManualImportPathPlanner.DetermineScanPath(group
                .Select(result => result.DestinationPath!)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList());

            if (string.IsNullOrWhiteSpace(scanPath))
            {
                _logger.LogDebug(
                    "No focused scan path could be determined for audiobook {AudiobookId} after manual import",
                    group.Key);
                continue;
            }

            await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                group.Key,
                async _ =>
                {
                    var audiobook = await _audiobookRepository.GetByIdAsync(group.Key);
                    if (audiobook == null)
                    {
                        _logger.LogWarning(
                            "Audiobook {AudiobookId} disappeared before its focused manual-import scan could be queued",
                            group.Key);
                        return;
                    }

                    await PersistAudiobookBasePathAsync(audiobook, scanPath);

                    try
                    {
                        var scanJobId = await _scanQueueService.EnqueueScanAsync(audiobook, scanPath);
                        _logger.LogInformation(
                            "Enqueued focused scan {ScanJobId} for audiobook {AudiobookId} (path: {Path}) after manual import batch of {FileCount} file(s)",
                            scanJobId,
                            group.Key,
                            scanPath,
                            group.Count());
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
                    }
                });
        }
    }

    private async Task PersistAudiobookBasePathAsync(Audiobook audiobook, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return;
        }

        try
        {
            basePath = FileUtils.NormalizeStoredPath(basePath);
            if (_fileSystem.FileExists(basePath))
            {
                basePath = Path.GetDirectoryName(basePath);
            }

            if (!string.IsNullOrWhiteSpace(basePath)
                && !string.Equals(audiobook.BasePath, basePath, StringComparison.Ordinal))
            {
                audiobook.BasePath = basePath;
                await _audiobookRepository.UpdateAsync(audiobook);
                _logger.LogInformation(
                    "Updated audiobook {AudiobookId} BasePath to {BasePath}",
                    audiobook.Id,
                    basePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to persist {BasePath} for audiobook {AudiobookId}", basePath, audiobook.Id);
        }
    }
}
