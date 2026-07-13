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

using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryMoveWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileSystem _fileSystem;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IMoveCleanupBoundaryResolver _cleanupBoundaryResolver;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly ILogger<LibraryMoveWorkflow> _logger;

        public LibraryMoveWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            IFileSystem fileSystem,
            ILogger<LibraryMoveWorkflow> logger,
            IFileSystemSemanticsResolver semanticsResolver,
            IMoveCleanupBoundaryResolver cleanupBoundaryResolver,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _fileSystem = fileSystem;
            _logger = logger;
            _semanticsResolver = semanticsResolver;
            _cleanupBoundaryResolver = cleanupBoundaryResolver;
            _destinationRewriteService = destinationRewriteService;
            _moveQueueService = moveQueueService;
        }

        public async Task<IActionResult> EnqueueAsync(int id, LibraryController.MoveRequest request)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return new NotFoundObjectResult(new { message = "Audiobook not found" });
            if (request == null) return new BadRequestObjectResult(new { message = "Request body is required" });

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is required" });
            }

            // Preserve valid Unix path-segment whitespace, but reject values that only become
            // absolute after trimming accidental leading whitespace. Otherwise move would treat
            // " /books/Title" as a relative child folder under the configured destination root.
            if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is invalid: leading whitespace before an absolute path is not allowed." });
            }

            if (request.MoveFiles == false)
            {
                try
                {
                    await _destinationRewriteService.RewriteDestinationAsync(
                        id,
                        request.DestinationPath,
                        request.SourcePath);
                    return new OkObjectResult(new { message = "Destination updated" });
                }
                catch (ListenarrApplicationException ex)
                {
                    return ToApplicationExceptionResult(ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to update BasePath for audiobook {AudiobookId}", id);
                    return new ObjectResult(new { message = "Failed to update BasePath", error = ex.Message })
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    };
                }
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
                await AddAllowedMoveRootAsync(allowedMoveRoots, normalizedOutputPath, FileSystemCaseSensitivityMode.Auto);

                string? defaultRootPath = null;
                foreach (var rootFolder in rootFolders)
                {
                    var normalizedRootPath = TryNormalizeMoveRoot(rootFolder.Path, $"root folder {rootFolder.Id}");
                    if (normalizedRootPath == null)
                    {
                        continue;
                    }

                    await AddAllowedMoveRootAsync(allowedMoveRoots, normalizedRootPath, rootFolder.CaseSensitivityMode);
                    if (rootFolder.IsDefault && defaultRootPath == null)
                    {
                        defaultRootPath = normalizedRootPath;
                    }
                }

                if (allowedMoveRoots.Count == 0)
                {
                    return new BadRequestObjectResult(new { message = "DestinationPath must be inside a configured root folder or output path" });
                }

                var destinationIsRooted = Path.IsPathRooted(request.DestinationPath!);
                var relativeMoveBase = normalizedOutputPath ?? defaultRootPath ?? allowedMoveRoots.FirstOrDefault()?.Path;
                if (!destinationIsRooted && string.IsNullOrEmpty(relativeMoveBase))
                {
                    return new BadRequestObjectResult(new { message = "DestinationPath requires a configured root folder or output path" });
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
                    return new BadRequestObjectResult(new { message = $"DestinationPath is invalid: {validationReason}" });
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
                        && _fileSystem.TryValidateMutationTarget(final, [customMutationRoot], out final, out finalReason);
                    if (!customPhysicalDestination)
                    {
                        _logger.LogWarning(
                            "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                            id,
                            final,
                            finalReason);
                        return new BadRequestObjectResult(new { message = "DestinationPath must be inside a configured root folder or output path" });
                    }
                }

                var targetBoundary = FindAllowedMoveRoot(final, allowedMoveRoots);
                if (targetBoundary == null && customPhysicalDestination)
                {
                    var customTargetResolution = await _semanticsResolver.ResolveAsync(final);
                    if (customTargetResolution.State != PathIdentityState.Valid)
                    {
                        return new BadRequestObjectResult(new { message = customTargetResolution.Reason ?? "Destination filesystem identity is unavailable." });
                    }

                    targetBoundary = new MoveRootBoundary(
                        customTargetResolution.BoundaryPath,
                        customTargetResolution.Semantics,
                        FileSystemCaseSensitivityMode.Auto);
                }

                if (targetBoundary == null)
                {
                    throw new InvalidOperationException("Destination filesystem identity is unavailable.");
                }

                var sourcePath = !string.IsNullOrEmpty(request.SourcePath)
                    ? request.SourcePath
                    : audiobook.BasePath;

                if (string.IsNullOrEmpty(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path not provided. Supply current source path in the Move request or ensure audiobook has a valid BasePath." });
                }

                if (FileUtils.IsPathInvalidForCurrentOs(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path is not valid for this operating system." });
                }

                if (!_fileSystem.DirectoryExists(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path does not exist. Ensure the audiobook's current BasePath exists or provide a valid SourcePath in the request." });
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
                    return new BadRequestObjectResult(new { message = "Target parent path is unavailable" });
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
                        return new BadRequestObjectResult(new { message = sourceResolution.Reason ?? "Source filesystem identity is unavailable." });
                    }

                    sourceIdentity = PathIdentitySnapshot.FromResolution(
                        sourceResolution.Semantics,
                        FileSystemCaseSensitivityMode.Auto,
                        sourceResolution.BoundaryPath,
                        sourceFull);
                }

                try
                {
                    if (AreSameMoveEndpoint(
                        sourceFull,
                        sourceIdentity,
                        final,
                        targetIdentity))
                    {
                        return new BadRequestObjectResult(new { message = "Source and target paths are identical; nothing to move." });
                    }
                }
                catch (Exception normalizeEx) when (
                    normalizeEx is ArgumentException
                    || normalizeEx is NotSupportedException
                    || normalizeEx is PathTooLongException
                    || normalizeEx is System.Security.SecurityException)
                {
                    _logger.LogDebug(normalizeEx, "Unable to normalize move paths for audiobook {AudiobookId}", id);
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

                var jobId = await _moveQueueService.EnqueueMoveAsync(
                    new MoveEnqueueCommand(
                        id,
                        sourceFull,
                        sourceIdentity,
                        final,
                        targetIdentity,
                        deleteEmptySource,
                        sourceCleanupBoundary));

                return new AcceptedResult(string.Empty, new { message = "Move enqueued", jobId });
            }
            catch (PersistenceException ex)
            {
                _logger.LogError(ex, "Move queue persistence failed while enqueueing move job for audiobook {AudiobookId}", id);
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
                _logger.LogWarning(ex, "Blocked move for audiobook {AudiobookId} during an active root folder relocation", id);
                return new ConflictObjectResult(new { message = ex.Message, code = "move_relocation_conflict" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
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

        public async Task<IActionResult> GetStatusAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });
            var job = await _moveQueueService.GetJobAsync(gid, cancellationToken);
            if (job != null)
            {
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job!.Status);
                return new OkObjectResult(job);
            }

            return new NotFoundObjectResult(new { message = "Job not found" });
        }

        public async Task<IActionResult> RequeueAsync(string jobId)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });

            Guid? newJobId;
            try
            {
                newJobId = await _moveQueueService.RequeueMoveAsync(gid);
            }
            catch (MoveRelocationConflictException ex)
            {
                return new ConflictObjectResult(new { message = ex.Message, code = "move_relocation_conflict" });
            }
            if (newJobId == null)
            {
                return new BadRequestObjectResult(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            return new AcceptedResult(string.Empty, new { message = "Requeued move job", jobId = newJobId });
        }

    }
}
