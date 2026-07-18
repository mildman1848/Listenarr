using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public async Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureResolved(semantics);

        string canonicalPath;
        string lookupKey;
        try
        {
            canonicalPath = FileSystemPathIdentity.Canonicalize(path, semantics.Syntax);
            lookupKey = FileSystemPathIdentity.CreateLookupKey(
                IdentityScope,
                canonicalPath,
                semantics.Syntax);
        }
        catch (ArgumentException exception)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unavailable,
                Reason: exception.Message);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unowned);
        }

        var compatible = new List<LibraryDirectoryOwnership>();
        var hasConflict = false;
        var hasUnavailable = false;
        foreach (var candidate in candidates)
        {
            try
            {
                var comparison = Compare(candidate, canonicalPath, semantics);
                if (comparison == OwnershipComparison.Compatible
                    && candidate.State is LibraryDirectoryOwnershipState.Owned
                        or LibraryDirectoryOwnershipState.Retained
                        or LibraryDirectoryOwnershipState.Removing)
                {
                    compatible.Add(candidate);
                }
                else if (comparison is OwnershipComparison.Compatible or OwnershipComparison.Conflict
                    || candidate.State == LibraryDirectoryOwnershipState.Conflict)
                {
                    hasConflict = true;
                }
                else if (candidate.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    hasUnavailable = true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return new LibraryDirectoryOwnershipResolution(
                    LibraryDirectoryOwnershipResolutionState.Unavailable,
                    Reason: $"The durable directory ownership identity is invalid: {exception.Message}");
            }
        }

        if (hasConflict || compatible.Count > 1)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Conflict,
                Reason: "Multiple or incompatible durable ownership claims resolve to this directory.");
        }
        if (hasUnavailable)
        {
            return new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unavailable,
                Reason: "Durable directory ownership exists but is unavailable for safe use.");
        }

        return compatible.Count == 1
            ? new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Owned,
                compatible[0])
            : new LibraryDirectoryOwnershipResolution(
                LibraryDirectoryOwnershipResolutionState.Unowned);
    }

    public async Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
        string basePath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        EnsureResolved(semantics);
        var canonicalBase = FileSystemPathIdentity.Canonicalize(basePath, semantics.Syntax);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.PathSyntax == semantics.Syntax
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        var owned = new List<LibraryDirectoryOwnership>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathIdentitySnapshot identity;
            try
            {
                identity = candidate.GetIdentity();
                identity.ValidateForPath(candidate.CanonicalPath);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "A durable directory ownership identity is invalid.",
                    exception);
            }

            var overlapsUnderCurrent = FileSystemPathIdentity.IsSameOrInside(
                candidate.CanonicalPath,
                canonicalBase,
                semantics);
            var overlapsUnderStored = FileSystemPathIdentity.IsSameOrInside(
                candidate.CanonicalPath,
                canonicalBase,
                identity.Semantics);
            if (!overlapsUnderCurrent && !overlapsUnderStored)
            {
                continue;
            }

            if (!overlapsUnderCurrent
                || !overlapsUnderStored
                || identity.CaseSensitivity != semantics.CaseSensitivity
                || candidate.State is LibraryDirectoryOwnershipState.Conflict
                    or LibraryDirectoryOwnershipState.Unavailable)
            {
                throw new InvalidOperationException(
                    "A conflicting or unavailable durable directory ownership claim overlaps the move source.");
            }

            owned.Add(candidate);
        }

        return owned
            .OrderBy(ownership => ownership.CanonicalPath.Length)
            .ToList();
    }
}
