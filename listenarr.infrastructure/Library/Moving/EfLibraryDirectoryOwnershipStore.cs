using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider) : ILibraryDirectoryOwnershipStore
{
    private const string IdentityScope = "library-directory";

    public async Task<LibraryDirectoryOwnership> RecordCreatedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.CreationWorkflow);
        EnsureResolved(claim.Semantics);

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            claim.Path,
            claim.Semantics.Syntax);
        var lookupKey = FileSystemPathIdentity.CreateLookupKey(
            IdentityScope,
            canonicalPath,
            claim.Semantics.Syntax);
        var ownershipKey = FileSystemPathIdentity.CreateKey(
            IdentityScope,
            canonicalPath,
            claim.Semantics);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var retiredCandidates = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State == LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);
        var candidates = await db.LibraryDirectoryOwnerships
            .Where(ownership => ownership.PathIdentityLookupKey == lookupKey
                && ownership.State != LibraryDirectoryOwnershipState.Removed)
            .ToListAsync(cancellationToken);

        var compatible = new List<LibraryDirectoryOwnership>();
        var conflicts = new List<LibraryDirectoryOwnership>();
        foreach (var candidate in candidates)
        {
            var comparison = Compare(candidate, canonicalPath, claim.Semantics);
            if (comparison == OwnershipComparison.Compatible
                && candidate.State is LibraryDirectoryOwnershipState.Owned
                    or LibraryDirectoryOwnershipState.Retained)
            {
                compatible.Add(candidate);
            }
            else if (comparison is OwnershipComparison.Compatible or OwnershipComparison.Conflict)
            {
                conflicts.Add(candidate);
            }
        }

        if (compatible.Count == 1 && conflicts.Count == 0)
        {
            var existing = compatible[0];
            existing.State = LibraryDirectoryOwnershipState.Owned;
            existing.StateReason = null;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            CleanupRetiredSiblingMarkers(
                retiredCandidates,
                canonicalPath,
                claim.Semantics);
            await LibraryDirectoryOwnershipMarker.EnsureAsync(existing, cancellationToken);
            return existing;
        }

        if (compatible.Count > 1 || conflicts.Count > 0)
        {
            foreach (var candidate in compatible.Concat(conflicts).DistinctBy(item => item.Id))
            {
                candidate.State = LibraryDirectoryOwnershipState.Conflict;
                candidate.PathOwnershipKey = null;
                candidate.StateReason = "Conflicting durable ownership claims resolve to this directory.";
                candidate.UpdatedAt = now;
            }

            db.LibraryDirectoryOwnerships.Add(CreateOwnership(
                claim,
                canonicalPath,
                lookupKey,
                ownershipKey: null,
                LibraryDirectoryOwnershipState.Conflict,
                "Conflicting durable ownership claims resolve to this directory.",
                now));
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            throw new InvalidOperationException(
                "The created library directory conflicts with an existing durable ownership claim.");
        }

        var ownership = CreateOwnership(
            claim,
            canonicalPath,
            lookupKey,
            ownershipKey,
            LibraryDirectoryOwnershipState.Owned,
            reason: null,
            now);
        db.LibraryDirectoryOwnerships.Add(ownership);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            CleanupRetiredSiblingMarkers(
                retiredCandidates,
                canonicalPath,
                claim.Semantics);
            await LibraryDirectoryOwnershipMarker.EnsureAsync(ownership, cancellationToken);
            return ownership;
        }
        catch (UniqueConstraintViolationException)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            await using var retryDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var concurrent = await retryDb.LibraryDirectoryOwnerships
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.PathOwnershipKey == ownershipKey,
                    cancellationToken);
            if (concurrent != null
                && Compare(concurrent, canonicalPath, claim.Semantics) == OwnershipComparison.Compatible)
            {
                CleanupRetiredSiblingMarkers(
                    retiredCandidates,
                    canonicalPath,
                    claim.Semantics);
                await LibraryDirectoryOwnershipMarker.EnsureAsync(concurrent, cancellationToken);
                return concurrent;
            }
            throw;
        }
    }

    private static void CleanupRetiredSiblingMarkers(
        IEnumerable<LibraryDirectoryOwnership> retiredCandidates,
        string canonicalPath,
        FileSystemPathSemantics semantics)
    {
        foreach (var retired in retiredCandidates)
        {
            try
            {
                if (Compare(retired, canonicalPath, semantics) == OwnershipComparison.Compatible)
                {
                    LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
                        retired,
                        out _);
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                // Removed rows are nonauthoritative. Corrupt retired metadata must not
                // prevent a new, independently proven ownership claim for the live path.
            }
        }
    }

    private static LibraryDirectoryOwnership CreateOwnership(
        LibraryDirectoryOwnershipClaim claim,
        string canonicalPath,
        string lookupKey,
        string? ownershipKey,
        LibraryDirectoryOwnershipState state,
        string? reason,
        DateTime now) => new()
        {
            Path = claim.Path,
            CanonicalPath = canonicalPath,
            PathSyntax = claim.Semantics.Syntax,
            PathCaseSensitivity = claim.Semantics.CaseSensitivity,
            PathCaseSensitivityMode = claim.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivityMode.Sensitive
                : FileSystemCaseSensitivityMode.Insensitive,
            PathIdentityBoundary = canonicalPath,
            PathIdentityLookupKey = lookupKey,
            PathOwnershipKey = ownershipKey,
            OwnershipToken = Guid.NewGuid().ToString("N"),
            State = state,
            CreationWorkflow = claim.CreationWorkflow,
            CreationOperationId = claim.CreationOperationId,
            AudiobookId = claim.AudiobookId,
            StateReason = reason,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static OwnershipComparison Compare(
        LibraryDirectoryOwnership ownership,
        string canonicalPath,
        FileSystemPathSemantics currentSemantics)
    {
        var identity = ownership.GetIdentity();
        identity.ValidateForPath(ownership.CanonicalPath);
        if (identity.Syntax != currentSemantics.Syntax)
        {
            return OwnershipComparison.Distinct;
        }

        var matchesCurrent = FileSystemPathIdentity.AreEquivalent(
            ownership.CanonicalPath,
            canonicalPath,
            currentSemantics);
        var matchesStored = FileSystemPathIdentity.AreEquivalent(
            ownership.CanonicalPath,
            canonicalPath,
            identity.Semantics);
        if (!matchesCurrent && !matchesStored)
        {
            return OwnershipComparison.Distinct;
        }

        return matchesCurrent
            && matchesStored
            && identity.CaseSensitivity == currentSemantics.CaseSensitivity
            && FileSystemPathIdentity.AreEquivalent(
                identity.BoundaryPath,
                canonicalPath,
                currentSemantics)
                ? OwnershipComparison.Compatible
                : OwnershipComparison.Conflict;
    }

    private static void EnsureResolved(FileSystemPathSemantics semantics)
    {
        if (semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Filesystem case sensitivity must be resolved before claiming directory ownership.");
        }
    }

    private enum OwnershipComparison
    {
        Distinct,
        Compatible,
        Conflict
    }
}
