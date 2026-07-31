using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    LibraryDirectoryOwnershipBoundaryAuthorizer? boundaryAuthorizer = null)
    : ILibraryDirectoryOwnershipStore
{
    private const string IdentityScope = "library-directory";
    private readonly LibraryDirectoryOwnershipBoundaryAuthorizer _boundaryAuthorizer =
        boundaryAuthorizer
        ?? new LibraryDirectoryOwnershipBoundaryAuthorizer(dbContextFactory);

    internal Action? AfterInsideOwnershipMarkerPublicationForTest
    {
        get;
        set;
    }

    internal Action? AfterOwnershipMarkerPublicationForTest
    {
        get;
        set;
    }

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
        await ThrowIfPersistedPathConflictAsync(
            canonicalPath,
            claim.Semantics,
            cancellationToken);
        using var authorization = await _boundaryAuthorizer.AuthorizeContainingRootAsync(
            claim.Path,
            claim.Semantics,
            cancellationToken);
        using var existing = authorization.ParentAnchor.OpenExistingChildForPublication(
            Path.GetFileName(canonicalPath));
        return await RecordCreatedCoreAsync(
            claim,
            existing,
            pinnedCreationIsNew: false,
            authorization.RootFolderId,
            cancellationToken);
    }

    public async Task<LibraryDirectoryOwnership> ClaimRetainedAsync(
        LibraryDirectoryOwnershipClaim claim,
        CancellationToken cancellationToken = default) =>
        await RecordCreatedAsync(claim, cancellationToken);

    internal Task<LibraryDirectoryOwnership> RecordPinnedCreatedAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation pinnedCreation,
        int managedRootFolderId,
        CancellationToken cancellationToken = default) =>
        RecordCreatedCoreAsync(
            claim,
            pinnedCreation,
            pinnedCreationIsNew: true,
            managedRootFolderId,
            cancellationToken);

    private Task<LibraryDirectoryOwnership> RepairPinnedExistingAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation pinnedCreation,
        int managedRootFolderId,
        CancellationToken cancellationToken = default) =>
        RecordCreatedCoreAsync(
            claim,
            pinnedCreation,
            pinnedCreationIsNew: false,
            managedRootFolderId,
            cancellationToken);

    private async Task<LibraryDirectoryOwnership> RecordCreatedCoreAsync(
        LibraryDirectoryOwnershipClaim claim,
        PinnedDirectoryCreation? pinnedCreation,
        bool pinnedCreationIsNew,
        int? managedRootFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.CreationWorkflow);
        EnsureResolved(claim.Semantics);

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            claim.Path,
            claim.Semantics.Syntax);
        using var existingPinnedCreation = pinnedCreation == null
            ? PinnedDirectoryCreation.OpenExistingForPublication(
                Path.GetDirectoryName(canonicalPath)
                    ?? throw new InvalidOperationException(
                        "The claimed directory has no parent directory."),
                Path.GetFileName(canonicalPath))
            : null;
        var markerCreation = pinnedCreation ?? existingPinnedCreation
            ?? throw new InvalidOperationException(
                "The claimed directory could not be pinned.");
        if (!markerCreation.Created
            || !FileSystemPathIdentity.AreEquivalent(
                markerCreation.FullPath,
                canonicalPath,
                claim.Semantics)
            || !markerCreation.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The claimed directory no longer matches its validated pathname.");
        }
        using var claimedDirectory = markerCreation.OpenCreatedDirectoryAnchor();
        var directoryObjectIdentity = claimedDirectory.GetDirectoryObjectIdentity();
        if (!claimedDirectory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The claimed directory changed while its physical identity was captured.");
        }

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
                    or LibraryDirectoryOwnershipState.Retained
                    or LibraryDirectoryOwnershipState.Unavailable)
            {
                compatible.Add(candidate);
            }
            else if (comparison is OwnershipComparison.Compatible or OwnershipComparison.Conflict)
            {
                conflicts.Add(candidate);
            }
        }

        if (pinnedCreationIsNew && (compatible.Count > 0 || conflicts.Count > 0))
        {
            throw new InvalidOperationException(
                "The newly created directory conflicts with an existing durable ownership claim.");
        }

        if (compatible.Count == 1 && conflicts.Count == 0)
        {
            var existing = compatible[0];
            EnsureAuthorizedPhysicalIdentity(
                existing,
                managedRootFolderId,
                directoryObjectIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            await PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
                existing,
                markerCreation,
                CancellationToken.None);
            ValidatePinnedOwnership(existing, markerCreation);
            existing.State = LibraryDirectoryOwnershipState.Owned;
            existing.StateReason = null;
            existing.DirectoryObjectIdentityUnavailableReason = null;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
            if (transaction != null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            CleanupRetiredSiblingMarkers(
                retiredCandidates,
                canonicalPath,
                claim.Semantics);
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
                managedRootFolderId,
                directoryObjectIdentity,
                now));
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
            throw new InvalidOperationException(
                "The created library directory conflicts with an existing durable ownership claim.");
        }

        var ownership = CreateOwnership(
            claim,
            canonicalPath,
            lookupKey,
            ownershipKey,
            LibraryDirectoryOwnershipState.Unavailable,
            "Durable ownership marker publication is pending.",
            managedRootFolderId,
            directoryObjectIdentity,
            now);
        ownership.DirectoryObjectIdentityUnavailableReason =
            "Durable ownership marker publication is pending.";
        db.LibraryDirectoryOwnerships.Add(ownership);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (transaction != null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
        }
        catch (UniqueConstraintViolationException)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            if (pinnedCreation != null)
            {
                throw new InvalidOperationException(
                    "The newly created directory lost a concurrent durable ownership race.");
            }

            await using var retryDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var concurrent = await retryDb.LibraryDirectoryOwnerships
                .SingleOrDefaultAsync(
                    candidate => candidate.PathOwnershipKey == ownershipKey,
                    cancellationToken);
            if (concurrent != null
                && Compare(concurrent, canonicalPath, claim.Semantics) == OwnershipComparison.Compatible)
            {
                EnsureAuthorizedPhysicalIdentity(
                    concurrent,
                    managedRootFolderId,
                    directoryObjectIdentity);
                cancellationToken.ThrowIfCancellationRequested();
                await PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
                    concurrent,
                    markerCreation,
                    CancellationToken.None);
                ValidatePinnedOwnership(concurrent, markerCreation);
                concurrent.State = LibraryDirectoryOwnershipState.Owned;
                concurrent.StateReason = null;
                concurrent.DirectoryObjectIdentityUnavailableReason = null;
                concurrent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await retryDb.SaveChangesAsync(CancellationToken.None);
                CleanupRetiredSiblingMarkers(
                    retiredCandidates,
                    canonicalPath,
                    claim.Semantics);
                return concurrent;
            }
            throw;
        }

        try
        {
            await PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
                ownership,
                markerCreation,
                CancellationToken.None,
                AfterInsideOwnershipMarkerPublicationForTest);
            AfterOwnershipMarkerPublicationForTest?.Invoke();
            ValidatePinnedOwnership(ownership, markerCreation);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            ownership.State = LibraryDirectoryOwnershipState.Unavailable;
            ownership.StateReason =
                "Durable ownership marker publication requires recovery.";
            ownership.DirectoryObjectIdentityUnavailableReason =
                exception.Message;
            ownership.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                // The committed pending row already preserves the ownership token.
                // Do not replace the original publication failure.
            }
            throw;
        }

        ownership.State = LibraryDirectoryOwnershipState.Owned;
        ownership.StateReason = null;
        ownership.DirectoryObjectIdentityUnavailableReason = null;
        ownership.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        CleanupRetiredSiblingMarkers(
            retiredCandidates,
            canonicalPath,
            claim.Semantics);
        return ownership;
    }

    private static void ValidatePinnedOwnership(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation creation)
    {
        using var directory = creation.OpenCreatedDirectoryAnchor();
        using var parent = creation.OpenParentDirectoryAnchor();
        LibraryDirectoryOwnershipMarker.Validate(
            ownership,
            directory,
            parent);
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
        int? managedRootFolderId,
        string nativeDirectoryIdentity,
        DateTime now)
    {
        var ownershipToken = Guid.NewGuid().ToString("N");
        return new LibraryDirectoryOwnership
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
            OwnershipToken = ownershipToken,
            State = state,
            CreationWorkflow = claim.CreationWorkflow,
            CreationOperationId = claim.CreationOperationId,
            AudiobookId = claim.AudiobookId,
            ManagedRootFolderId = managedRootFolderId,
            DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                nativeDirectoryIdentity),
            DirectoryObjectIdentityUnavailableReason = managedRootFolderId.HasValue
                ? null
                : "The claim was not created through an authorized managed root.",
            StateReason = reason,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void EnsureAuthorizedPhysicalIdentity(
        LibraryDirectoryOwnership ownership,
        int? managedRootFolderId,
        string directoryObjectIdentity)
    {
        if (!managedRootFolderId.HasValue
            || ownership.ManagedRootFolderId != managedRootFolderId
            || !ManagedDirectoryIdentity.Matches(
                ownership.DirectoryObjectIdentityVersion,
                ownership.DirectoryObjectIdentity,
                ownership.OwnershipToken,
                directoryObjectIdentity)
            || (ownership.State !=
                    LibraryDirectoryOwnershipState.Unavailable
                && !string.IsNullOrWhiteSpace(
                    ownership.DirectoryObjectIdentityUnavailableReason)))
        {
            throw new InvalidOperationException(
                "The existing ownership claim lacks matching managed-root and physical-directory authorization.");
        }
    }

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
