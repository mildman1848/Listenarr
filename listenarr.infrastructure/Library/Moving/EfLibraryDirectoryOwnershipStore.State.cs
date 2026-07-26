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

    public Task MarkRemovedAsync(
        long ownershipId,
        string expectedOwnershipKey,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(
            ownershipId,
            expectedOwnershipKey,
            [LibraryDirectoryOwnershipState.Removing],
            LibraryDirectoryOwnershipState.Removed,
            reason: null,
            clearOwnershipKey: true,
            cancellationToken);

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
