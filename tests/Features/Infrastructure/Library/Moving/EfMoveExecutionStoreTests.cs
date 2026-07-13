using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed class EfMoveExecutionStoreTests
{
    [Fact]
    public async Task ProviderFailures_AreTranslatedAcrossMoveExecutionBoundary()
    {
        var store = new EfMoveExecutionStore(
            new ThrowingDbContextFactory(),
            TimeProvider.System);
        var jobId = Guid.NewGuid();
        var lease = new MoveLeaseToken("worker", 1);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var source = Path.GetFullPath(Path.Join(Path.GetTempPath(), "move-store-source"));
        var target = Path.GetFullPath(Path.Join(Path.GetTempPath(), "move-store-target"));
        var operations = new Func<Task>[]
        {
            () => store.EnsureLeaseOwnedAsync(jobId, lease, CancellationToken.None),
            () => store.ValidateOrAdoptIdentityAsync(
                jobId,
                source,
                target,
                semantics,
                semantics,
                lease,
                hasFilesystemRecoveryArtifacts: false,
                CancellationToken.None),
            () => store.EnsureMutationAuthorizedAsync(
                jobId,
                lease,
                source,
                target,
                semantics,
                semantics,
                CancellationToken.None),
            () => store.PersistManifestAsync(jobId, lease, [], CancellationToken.None),
            async () => _ = await store.LoadManifestAsync(jobId, CancellationToken.None),
            () => store.UpdateCleanupStateAsync(
                jobId,
                lease,
                "book.m4b",
                MoveJobEntryCleanupState.Deleted,
                CancellationToken.None),
            () => store.UpdateCopyStateAsync(jobId, lease, CancellationToken.None),
            () => store.UpdateJobPhaseAsync(
                jobId,
                lease,
                MoveJobPhase.Published,
                CancellationToken.None),
            async () => _ = await store.GetCreatedDirectoriesAsync(jobId, CancellationToken.None),
            () => store.PersistCreatedDirectoriesAsync(
                jobId,
                lease,
                [Path.Join(target, "parent")],
                CancellationToken.None),
            () => store.UpdateCreatedDirectoryStateAsync(
                jobId,
                lease,
                Path.Join(target, "parent"),
                MoveCreatedDirectoryState.Created,
                CancellationToken.None)
        };

        foreach (var operation in operations)
        {
            var exception = await Assert.ThrowsAsync<PersistenceException>(operation);
            Assert.IsType<SimulatedProviderException>(exception.InnerException);
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() =>
            throw new SimulatedProviderException();

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<ListenArrDbContext>(new SimulatedProviderException());
    }

    private sealed class SimulatedProviderException : DbException;
}
