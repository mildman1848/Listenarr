using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public Task BeginRemovalAsync(
        long ownershipId,
        string expectedOwnershipKey,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(
            ownershipId,
            expectedOwnershipKey,
            [
                LibraryDirectoryOwnershipState.Owned,
                LibraryDirectoryOwnershipState.Retained,
                LibraryDirectoryOwnershipState.Removing
            ],
            LibraryDirectoryOwnershipState.Removing,
            reason: null,
            clearOwnershipKey: false,
            cancellationToken);

    public Task RetainAsync(
        long ownershipId,
        string expectedOwnershipKey,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(
            ownershipId,
            expectedOwnershipKey,
            [LibraryDirectoryOwnershipState.Removing],
            LibraryDirectoryOwnershipState.Retained,
            reason,
            clearOwnershipKey: false,
            cancellationToken);

    public async Task MarkRemovedAsync(
        long ownershipId,
        string expectedOwnershipKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnershipKey);
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var ownership = await db.LibraryDirectoryOwnerships
            .SingleOrDefaultAsync(
                candidate => candidate.Id == ownershipId
                    && candidate.PathOwnershipKey == expectedOwnershipKey,
                cancellationToken);
        if (ownership == null
            || ownership.State != LibraryDirectoryOwnershipState.Removing)
        {
            throw new InvalidOperationException(
                "The durable directory ownership claim changed before its cleanup state could be updated.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.LibraryDirectoryOwnershipRetiredMarkers.Add(
            LibraryDirectoryOwnershipRetiredMarkerEvidence.Create(
                ownership,
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    LibraryDirectoryOwnershipMarker.Version,
                    ownership.OwnershipToken,
                    ownership.CanonicalPath,
                    ownership.ManagedRootFolderId,
                    ownership.DirectoryObjectIdentityVersion,
                    ownership.DirectoryObjectIdentity),
                now));
        ownership.State = LibraryDirectoryOwnershipState.Removed;
        ownership.PathOwnershipKey = null;
        ownership.ManagedRootFolderId = null;
        ownership.StateReason = null;
        ownership.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    private async Task UpdateStateAsync(
        long ownershipId,
        string expectedOwnershipKey,
        IReadOnlyCollection<LibraryDirectoryOwnershipState> allowedStates,
        LibraryDirectoryOwnershipState targetState,
        string? reason,
        bool clearOwnershipKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnershipKey);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ownership = await db.LibraryDirectoryOwnerships.SingleOrDefaultAsync(
            candidate => candidate.Id == ownershipId
                && candidate.PathOwnershipKey == expectedOwnershipKey,
            cancellationToken);
        if (ownership == null || !allowedStates.Contains(ownership.State))
        {
            throw new InvalidOperationException(
                "The durable directory ownership claim changed before its cleanup state could be updated.");
        }
        if (targetState == LibraryDirectoryOwnershipState.Removing
            && !HasDestructiveIdentity(ownership))
        {
            throw new InvalidOperationException(
                "The ownership claim has no physical identity authorization for removal.");
        }

        ownership.State = targetState;
        if (clearOwnershipKey)
        {
            ownership.PathOwnershipKey = null;
        }
        ownership.StateReason = reason;
        ownership.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class LibraryDirectoryOwnershipRetiredMarkerEvidence
{
    public static LibraryDirectoryOwnershipRetiredMarker CreateLegacyPending(
        LibraryDirectoryOwnership ownership,
        int? originalManagedRootFolderId = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var managedRootFolderId =
            originalManagedRootFolderId ?? ownership.ManagedRootFolderId;
        var payloadVersion = managedRootFolderId.HasValue
            && ownership.DirectoryObjectIdentityVersion.HasValue
            && !string.IsNullOrWhiteSpace(ownership.DirectoryObjectIdentity)
                ? LibraryDirectoryOwnershipMarker.Version
                : 1;
        return new LibraryDirectoryOwnershipRetiredMarker
        {
            OwnershipId = ownership.Id,
            OwnershipToken = ownership.OwnershipToken,
            CanonicalMarkerPath = null,
            CanonicalOwnershipPath = ownership.CanonicalPath,
            PathSyntax = ownership.PathSyntax,
            PathCaseSensitivity = ownership.PathCaseSensitivity,
            PathCaseSensitivityMode = ownership.PathCaseSensitivityMode,
            PathIdentityBoundary = ownership.PathIdentityBoundary,
            CanonicalPayload = null,
            PayloadSha256 = null,
            PayloadVersion = payloadVersion,
            OriginalManagedRootFolderId = managedRootFolderId,
            DirectoryObjectIdentityVersion =
                ownership.DirectoryObjectIdentityVersion,
            DirectoryObjectIdentity = ownership.DirectoryObjectIdentity,
            State = LibraryDirectoryOwnershipRetiredMarkerState.Pending,
            CreatedAt = ownership.CreatedAt,
            UpdatedAt = ownership.UpdatedAt
        };
    }

    public static LibraryDirectoryOwnershipRetiredMarker Create(
        LibraryDirectoryOwnership ownership,
        LibraryDirectoryOwnershipMarker.MarkerPayload payload,
        DateTime now)
    {
        var canonicalPayload =
            LibraryDirectoryOwnershipMarker.SerializePayload(payload);
        var payloadSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalPayload)));
        return new LibraryDirectoryOwnershipRetiredMarker
        {
            OwnershipId = ownership.Id,
            OwnershipToken = payload.OwnershipToken,
            CanonicalMarkerPath =
                LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)[1],
            CanonicalOwnershipPath = ownership.CanonicalPath,
            PathSyntax = ownership.PathSyntax,
            PathCaseSensitivity = ownership.PathCaseSensitivity,
            PathCaseSensitivityMode = ownership.PathCaseSensitivityMode,
            PathIdentityBoundary = ownership.PathIdentityBoundary,
            CanonicalPayload = canonicalPayload,
            PayloadSha256 = payloadSha256,
            PayloadVersion = payload.Version,
            OriginalManagedRootFolderId = ownership.ManagedRootFolderId,
            DirectoryObjectIdentityVersion =
                ownership.DirectoryObjectIdentityVersion,
            DirectoryObjectIdentity = ownership.DirectoryObjectIdentity,
            State = LibraryDirectoryOwnershipRetiredMarkerState.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static bool Matches(
        LibraryDirectoryOwnershipRetiredMarker evidence,
        LibraryDirectoryOwnershipMarker.MarkerPayload payload)
    {
        var canonicalPayload =
            LibraryDirectoryOwnershipMarker.SerializePayload(payload);
        var checksum = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalPayload)));
        return string.Equals(
                canonicalPayload,
                evidence.CanonicalPayload,
                StringComparison.Ordinal)
            && string.Equals(
                checksum,
                evidence.PayloadSha256,
                StringComparison.Ordinal);
    }

    public static void MaterializeCanonicalPayload(
        LibraryDirectoryOwnershipRetiredMarker evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.CanonicalMarkerPath))
        {
            var parentPath = Path.GetDirectoryName(
                evidence.CanonicalOwnershipPath)
                ?? throw new InvalidOperationException(
                    "The retired ownership path has no parent directory.");
            evidence.CanonicalMarkerPath = Path.Join(
                parentPath,
                $".listenarr-directory-owner-{evidence.OwnershipToken}.json");
        }

        var payload = evidence.PayloadVersion == 1
            ? new LibraryDirectoryOwnershipMarker.MarkerPayload(
                1,
                evidence.OwnershipToken,
                evidence.CanonicalOwnershipPath)
            : new LibraryDirectoryOwnershipMarker.MarkerPayload(
                evidence.PayloadVersion,
                evidence.OwnershipToken,
                evidence.CanonicalOwnershipPath,
                evidence.OriginalManagedRootFolderId,
                evidence.DirectoryObjectIdentityVersion,
                evidence.DirectoryObjectIdentity);
        var canonicalPayload =
            LibraryDirectoryOwnershipMarker.SerializePayload(payload);
        evidence.CanonicalPayload = canonicalPayload;
        evidence.PayloadSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalPayload)));
    }
}
