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
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly ILogger<LibraryMoveWorkflow> _logger;

        public LibraryMoveWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            IFileSystem fileSystem,
            ILogger<LibraryMoveWorkflow> logger,
            IFileSystemSemanticsResolver semanticsResolver,
            IMoveCleanupBoundaryResolver cleanupBoundaryResolver,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IFilesystemMutationCoordinator mutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _fileSystem = fileSystem;
            _logger = logger;
            _semanticsResolver = semanticsResolver;
            _cleanupBoundaryResolver = cleanupBoundaryResolver;
            _destinationRewriteService = destinationRewriteService;
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _moveQueueService = moveQueueService;
        }

        public async Task<IActionResult> EnqueueAsync(
            int id,
            LibraryController.MoveRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
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
                        request.SourcePath,
                        cancellationToken);
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

            return await _mutationCoordinator.ExecuteExclusiveAsync(
                token => EnqueuePhysicalAsync(id, request, token),
                cancellationToken);
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
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job.Status);
                return new OkObjectResult(job);
            }

            return new NotFoundObjectResult(new { message = "Job not found" });
        }

        public async Task<IActionResult> RequeueAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });

            Guid? newJobId;
            try
            {
                newJobId = await _moveQueueService.RequeueMoveAsync(
                    gid,
                    cancellationToken);
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
