using Listenarr.Domain.Common;
using Listenarr.Infrastructure.FileSystem;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public async Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
        string destinationDirectory,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        string creationWorkflow,
        Guid? creationOperationId = null,
        int? audiobookId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedBoundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(creationWorkflow);
        EnsureResolved(semantics);

        var destination = FileSystemPathIdentity.Canonicalize(
            destinationDirectory,
            semantics.Syntax);
        var boundary = FileSystemPathIdentity.Canonicalize(
            managedBoundary,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(destination, boundary, semantics))
        {
            throw new InvalidOperationException(
                "The directory creation destination is outside its managed boundary.");
        }
        if (!Directory.Exists(boundary))
        {
            throw new InvalidOperationException(
                "The managed directory creation boundary does not exist.");
        }
        ValidateExistingDirectory(
            boundary,
            "managed directory creation boundary",
            allowReparsePoint: true);

        var hierarchy = new List<string>();
        var current = destination;
        while (!FileSystemPathIdentity.AreEquivalent(current, boundary, semantics))
        {
            hierarchy.Add(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    "The directory creation destination has no parent inside its managed boundary.");
            if (!FileSystemPathIdentity.IsSameOrInside(current, boundary, semantics))
            {
                throw new InvalidOperationException(
                    "The directory creation hierarchy escaped its managed boundary.");
            }
        }
        hierarchy.Reverse();

        var createdOwnerships = new List<LibraryDirectoryOwnership>();
        foreach (var directory in hierarchy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(directory)
                ?? throw new InvalidOperationException(
                    "The directory creation target has no parent.");
            ValidateExistingDirectory(
                parent,
                "directory creation parent",
                allowReparsePoint: FileSystemPathIdentity.AreEquivalent(
                    parent,
                    boundary,
                    semantics));
            if (File.Exists(directory))
            {
                throw new InvalidOperationException(
                    "The directory creation target is occupied by a file.");
            }

            using var creation = ExclusiveDirectoryCreator.TryCreatePinned(
                parent,
                Path.GetFileName(directory));
            if (!creation.Created)
            {
                ValidateExistingDirectory(
                    directory,
                    "existing directory creation target",
                    allowReparsePoint: false);
                var existingResolution = await ResolveOwnedAsync(
                    directory,
                    semantics,
                    cancellationToken);
                if (existingResolution.State == LibraryDirectoryOwnershipResolutionState.Owned)
                {
                    await RecordCreatedAsync(
                        new LibraryDirectoryOwnershipClaim(
                            directory,
                            semantics,
                            creationWorkflow,
                            creationOperationId,
                            audiobookId),
                        cancellationToken);
                }
                else if (existingResolution.State is
                    LibraryDirectoryOwnershipResolutionState.Conflict
                    or LibraryDirectoryOwnershipResolutionState.Unavailable)
                {
                    throw new InvalidOperationException(
                        existingResolution.Reason
                            ?? "Existing directory ownership is conflicting or unavailable.");
                }
                continue;
            }

            // The pinned parent and child handles stay alive through database persistence and
            // marker publication. External pathname replacement therefore cannot redirect the
            // creation or either ownership proof outside the validated parent directory.
            try
            {
                createdOwnerships.Add(await RecordPinnedCreatedAsync(
                    new LibraryDirectoryOwnershipClaim(
                        directory,
                        semantics,
                        creationWorkflow,
                        creationOperationId,
                        audiobookId),
                    creation,
                    CancellationToken.None));
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                // Path-based compensation is allowed only while the visible path still
                // identifies the pinned generation. A replaced pathname is preserved.
                if (creation.VisiblePathMatches())
                {
                    await TryCompensateFailedExclusiveCreationAsync(
                        directory,
                        parent,
                        semantics);
                }
                throw;
            }
        }

        return createdOwnerships;
    }

    private async Task TryCompensateFailedExclusiveCreationAsync(
        string directory,
        string parent,
        FileSystemPathSemantics semantics)
    {
        try
        {
            var resolution = await ResolveOwnedAsync(
                directory,
                semantics,
                CancellationToken.None);
            if (resolution.State != LibraryDirectoryOwnershipResolutionState.Unowned)
            {
                return;
            }

            FileSystemSafety.TryDeleteEmptyDirectory(
                directory,
                [parent],
                out _);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            // Compensation is deliberately best effort and fail closed. The original
            // ownership failure remains authoritative, and any uncertain or changed path
            // is preserved rather than recursively cleaned or adopted on retry.
        }
    }

    private static void ValidateExistingDirectory(
        string path,
        string description,
        bool allowReparsePoint)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"The {description} does not exist.");
        }

        var attributes = File.GetAttributes(path);
        if (!allowReparsePoint && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The {description} is a symbolic link or reparse point.");
        }
    }
}
