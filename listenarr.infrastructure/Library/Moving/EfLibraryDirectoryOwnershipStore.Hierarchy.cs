using Listenarr.Domain.Common;

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
        var currentAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(boundary);
        try
        {
            foreach (var directory in hierarchy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!currentAnchor.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The visible directory hierarchy changed after its boundary was pinned.");
                }

                var childName = Path.GetFileName(directory);
                using var creation = currentAnchor.TryCreateChild(childName);
                PinnedDirectoryCreation.PinnedDirectoryAnchor nextAnchor;
                if (!creation.Created)
                {
                    nextAnchor = currentAnchor.OpenExistingChild(childName);
                    try
                    {
                        EnsureVisibleAnchor(nextAnchor);
                        var existingResolution = await ResolveOwnedAsync(
                            directory,
                            semantics,
                            cancellationToken);
                        EnsureVisibleAnchor(nextAnchor);
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
                            EnsureVisibleAnchor(nextAnchor);
                        }
                        else if (existingResolution.State is
                            LibraryDirectoryOwnershipResolutionState.Conflict
                            or LibraryDirectoryOwnershipResolutionState.Unavailable)
                        {
                            throw new InvalidOperationException(
                                existingResolution.Reason
                                    ?? "Existing directory ownership is conflicting or unavailable.");
                        }
                    }
                    catch
                    {
                        nextAnchor.Dispose();
                        throw;
                    }
                }
                else
                {
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
                        if (!creation.VisiblePathMatches())
                        {
                            throw new InvalidOperationException(
                                "The visible created directory changed before hierarchy continuation.");
                        }

                        var createdAnchor = creation.OpenCreatedDirectoryAnchor();
                        try
                        {
                            EnsureVisibleAnchor(createdAnchor);
                            nextAnchor = createdAnchor;
                        }
                        catch
                        {
                            createdAnchor.Dispose();
                            throw;
                        }
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
                                currentAnchor.FullPath,
                                semantics);
                        }
                        throw;
                    }
                }

                currentAnchor.Dispose();
                currentAnchor = nextAnchor;
            }

            return createdOwnerships;
        }
        finally
        {
            currentAnchor.Dispose();
        }
    }

    private static void EnsureVisibleAnchor(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor)
    {
        if (!anchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The visible directory hierarchy changed while a component was pinned.");
        }
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
