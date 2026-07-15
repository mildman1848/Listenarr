using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMoveWorkflow
{
    private async Task<IActionResult> EnqueuePhysicalAsync(
        int id,
        LibraryController.MoveRequest request)
    {
        var audiobook = await _repo.GetByIdAsync(id);
        if (audiobook == null)
        {
            return new NotFoundObjectResult(new { message = "Audiobook not found" });
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var rootFolderService = scope.ServiceProvider.GetRequiredService<IRootFolderService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var rootFolders = await rootFolderService.GetAllAsync();

            var allowedMoveRoots = new List<MoveRootBoundary>();
            var normalizedOutputPath = TryNormalizeMoveRoot(settings.OutputPath, "configured output path");
            await AddAllowedMoveRootAsync(
                allowedMoveRoots,
                normalizedOutputPath,
                FileSystemCaseSensitivityMode.Auto);

            string? defaultRootPath = null;
            foreach (var rootFolder in rootFolders)
            {
                var normalizedRootPath = TryNormalizeMoveRoot(
                    rootFolder.Path,
                    $"root folder {rootFolder.Id}");
                if (normalizedRootPath == null)
                {
                    continue;
                }

                await AddAllowedMoveRootAsync(
                    allowedMoveRoots,
                    normalizedRootPath,
                    rootFolder.CaseSensitivityMode);
                if (rootFolder.IsDefault && defaultRootPath == null)
                {
                    defaultRootPath = normalizedRootPath;
                }
            }

            if (allowedMoveRoots.Count == 0)
            {
                return new BadRequestObjectResult(new
                {
                    message = "DestinationPath must be inside a configured root folder or output path"
                });
            }

            var destinationIsRooted = Path.IsPathRooted(request.DestinationPath!);
            var relativeMoveBase = normalizedOutputPath
                ?? defaultRootPath
                ?? allowedMoveRoots.FirstOrDefault()?.Path;
            if (!destinationIsRooted && string.IsNullOrEmpty(relativeMoveBase))
            {
                return new BadRequestObjectResult(new
                {
                    message = "DestinationPath requires a configured root folder or output path"
                });
            }

            var destinationCandidate = destinationIsRooted
                ? request.DestinationPath!
                : FileUtils.CombineWithOptionalBase(relativeMoveBase, request.DestinationPath!);
            if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                    destinationCandidate,
                    out var final,
                    out var validationReason,
                    rejectParentTraversal: true))
            {
                return new BadRequestObjectResult(new
                {
                    message = $"DestinationPath is invalid: {validationReason}"
                });
            }

            var destinationInsideConfiguredBoundary = _fileSystem.TryValidateMutationTarget(
                final,
                allowedMoveRoots.Select(root => root.Path),
                out final,
                out var finalReason);
            var customPhysicalDestination = false;
            if (!destinationInsideConfiguredBoundary)
            {
                var customMutationRoot = TryFindNearestExistingDirectory(final);
                customPhysicalDestination = !string.IsNullOrEmpty(customMutationRoot)
                    && _fileSystem.TryValidateMutationTarget(
                        final,
                        [customMutationRoot],
                        out final,
                        out finalReason);
                if (!customPhysicalDestination)
                {
                    _logger.LogWarning(
                        "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                        id,
                        final,
                        finalReason);
                    return new BadRequestObjectResult(new
                    {
                        message = "DestinationPath must be inside a configured root folder or output path"
                    });
                }
            }

            var targetBoundary = FindAllowedMoveRoot(final, allowedMoveRoots);
            if (targetBoundary == null && customPhysicalDestination)
            {
                var customTargetResolution = await _semanticsResolver.ResolveAsync(final);
                if (customTargetResolution.State != PathIdentityState.Valid)
                {
                    return new BadRequestObjectResult(new
                    {
                        message = customTargetResolution.Reason
                            ?? "Destination filesystem identity is unavailable."
                    });
                }

                targetBoundary = new MoveRootBoundary(
                    customTargetResolution.BoundaryPath,
                    customTargetResolution.Semantics,
                    FileSystemCaseSensitivityMode.Auto);
            }

            if (targetBoundary == null)
            {
                throw new InvalidOperationException(
                    "Destination filesystem identity is unavailable.");
            }

            // SourcePath is an expected-state token, not permission to move an arbitrary
            // existing directory. The audiobook's persisted BasePath remains authoritative.
            var sourcePath = audiobook.BasePath;
            if (string.IsNullOrEmpty(sourcePath))
            {
                return new BadRequestObjectResult(new
                {
                    message = "The audiobook has no current source path."
                });
            }

            if (FileUtils.IsPathInvalidForCurrentOs(sourcePath))
            {
                return new BadRequestObjectResult(new
                {
                    message = "The audiobook source path is not valid for this operating system."
                });
            }

            if (!_fileSystem.DirectoryExists(sourcePath))
            {
                return new BadRequestObjectResult(new
                {
                    message = "Source path does not exist for the audiobook."
                });
            }

            var targetParent = Path.GetDirectoryName(final);
            if (string.IsNullOrEmpty(targetParent))
            {
                return new BadRequestObjectResult(new { message = "Invalid target path" });
            }

            var nearestTargetAncestor = TryFindNearestExistingDirectory(targetParent);
            var ancestorReason = "No existing target ancestor is available.";
            if (string.IsNullOrWhiteSpace(nearestTargetAncestor)
                || !_fileSystem.TryValidateMutationTarget(
                    final,
                    [nearestTargetAncestor],
                    out final,
                    out ancestorReason))
            {
                _logger.LogWarning(
                    "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                    id,
                    final,
                    ancestorReason);
                return new BadRequestObjectResult(new
                {
                    message = "Target parent path is unavailable"
                });
            }

            var sourceFull = Path.GetFullPath(sourcePath);
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetBoundary.Semantics,
                targetBoundary.CaseSensitivityMode,
                targetBoundary.Path,
                final);
            var sourceBoundary = FindAllowedMoveRoot(sourceFull, allowedMoveRoots);
            PathIdentitySnapshot sourceIdentity;
            if (sourceBoundary != null)
            {
                sourceIdentity = PathIdentitySnapshot.FromResolution(
                    sourceBoundary.Semantics,
                    sourceBoundary.CaseSensitivityMode,
                    sourceBoundary.Path,
                    sourceFull);
            }
            else
            {
                var sourceResolution = await _semanticsResolver.ResolveAsync(sourceFull);
                if (sourceResolution.State != PathIdentityState.Valid)
                {
                    return new BadRequestObjectResult(new
                    {
                        message = sourceResolution.Reason
                            ?? "Source filesystem identity is unavailable."
                    });
                }

                sourceIdentity = PathIdentitySnapshot.FromResolution(
                    sourceResolution.Semantics,
                    FileSystemCaseSensitivityMode.Auto,
                    sourceResolution.BoundaryPath,
                    sourceFull);
            }

            if (!string.IsNullOrWhiteSpace(request.SourcePath))
            {
                if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                        request.SourcePath,
                        out var expectedSourceFull,
                        out var expectedSourceReason,
                        rejectParentTraversal: true))
                {
                    return new BadRequestObjectResult(new
                    {
                        message = $"SourcePath is invalid: {expectedSourceReason}"
                    });
                }

                if (!FileSystemPathIdentity.AreEquivalent(
                        expectedSourceFull,
                        sourceFull,
                        sourceIdentity.Semantics))
                {
                    return new ConflictObjectResult(new
                    {
                        message = "The audiobook source path changed. Refresh and try again.",
                        code = "source_path_changed"
                    });
                }
            }

            try
            {
                if (AreSameMoveEndpoint(
                        sourceFull,
                        sourceIdentity,
                        final,
                        targetIdentity))
                {
                    return new BadRequestObjectResult(new
                    {
                        message = "Source and target paths are identical; nothing to move."
                    });
                }
            }
            catch (Exception normalizeEx) when (
                normalizeEx is ArgumentException
                || normalizeEx is NotSupportedException
                || normalizeEx is PathTooLongException
                || normalizeEx is System.Security.SecurityException)
            {
                _logger.LogDebug(
                    normalizeEx,
                    "Unable to normalize move paths for audiobook {AudiobookId}",
                    id);
            }

            var deleteEmptySource = request.DeleteEmptySource ?? true;
            string? sourceCleanupBoundary = null;
            if (deleteEmptySource)
            {
                var cleanupBoundary = await _cleanupBoundaryResolver.ResolveAsync(
                    sourcePath,
                    final,
                    rootFolders);
                sourceCleanupBoundary = cleanupBoundary.Boundary;
                if (!cleanupBoundary.IsAvailable)
                {
                    _logger.LogWarning(
                        "Move for audiobook {AudiobookId} has no safe source cleanup boundary: {Reason}",
                        id,
                        cleanupBoundary.Reason ?? "boundary unavailable");
                }
                else
                {
                    _logger.LogInformation(
                        "Resolved {BoundaryKind} source cleanup boundary {Boundary} for audiobook {AudiobookId}",
                        cleanupBoundary.Kind,
                        LogRedaction.SanitizeFilePath(sourceCleanupBoundary),
                        id);
                }
            }

            var enqueueCommand = new MoveEnqueueCommand(
                id,
                sourceFull,
                sourceIdentity,
                final,
                targetIdentity,
                deleteEmptySource,
                sourceCleanupBoundary);
            var jobId = await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                id,
                async lockedToken =>
                {
                    using var authoritativeScope = _scopeFactory.CreateScope();
                    var authoritativeRepository = authoritativeScope.ServiceProvider
                        .GetRequiredService<IAudiobookRepository>();
                    var currentAudiobook = await authoritativeRepository.GetByIdSnapshotAsync(
                        id,
                        lockedToken)
                        ?? throw new ApplicationNotFoundException(
                            "audiobook_not_found",
                            "Audiobook not found");
                    if (string.IsNullOrWhiteSpace(currentAudiobook.BasePath)
                        || !SourceStateMatches(
                            currentAudiobook.BasePath,
                            sourceFull,
                            sourceIdentity.Semantics))
                    {
                        throw new ApplicationConflictException(
                            "source_path_changed",
                            "The audiobook source path changed. Refresh and try again.");
                    }

                    return await _moveQueueService!.EnqueueMoveAsync(
                        enqueueCommand,
                        lockedToken);
                },
                CancellationToken.None);

            return new AcceptedResult(
                string.Empty,
                new { message = "Move enqueued", jobId });
        }
        catch (PersistenceException ex)
        {
            _logger.LogError(
                ex,
                "Move queue persistence failed while enqueueing move job for audiobook {AudiobookId}",
                id);
            return new ObjectResult(new
            {
                message = "Move queue persistence is unavailable. Check database migrations.",
                code = "move_queue_persistence_unavailable"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
        catch (MoveRelocationConflictException ex)
        {
            _logger.LogWarning(
                ex,
                "Blocked move for audiobook {AudiobookId} during an active root folder relocation",
                id);
            return new ConflictObjectResult(new
            {
                message = ex.Message,
                code = "move_relocation_conflict"
            });
        }
        catch (ListenarrApplicationException ex)
        {
            return ToApplicationExceptionResult(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Failed to enqueue move job for audiobook {AudiobookId}", id);
            return new ObjectResult(new
            {
                message = "Failed to enqueue move job",
                code = "move_enqueue_failed"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
