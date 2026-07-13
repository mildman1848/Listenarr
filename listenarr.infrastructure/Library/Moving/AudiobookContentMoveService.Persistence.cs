using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private Task EnsureLeaseOwnedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.EnsureLeaseOwnedAsync(jobId, leaseToken, cancellationToken);

    private Task ValidatePersistedMoveIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.ValidateOrAdoptIdentityAsync(
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            HasLegacyFilesystemRecoveryArtifacts(source, target, jobId),
            cancellationToken);

    private Task PersistManifestAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<MoveJobEntry> manifest,
        CancellationToken cancellationToken) =>
        executionStore.PersistManifestAsync(
            jobId,
            leaseToken,
            manifest,
            cancellationToken);

    private Task<List<MoveJobEntry>> LoadManifestAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.LoadManifestAsync(jobId, cancellationToken);

    private Task UpdateCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCleanupStateAsync(
            jobId,
            leaseToken,
            relativePath,
            cleanupState,
            cancellationToken);

    private Task UpdateCopyStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCopyStateAsync(jobId, leaseToken, cancellationToken);

    private Task UpdateJobPhaseAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobPhase phase,
        CancellationToken cancellationToken) =>
        executionStore.UpdateJobPhaseAsync(
            jobId,
            leaseToken,
            phase,
            cancellationToken);

    private Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        executionStore.GetCreatedDirectoriesAsync(jobId, cancellationToken);

    private Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken) =>
        executionStore.PersistCreatedDirectoriesAsync(
            jobId,
            leaseToken,
            paths,
            cancellationToken);

    private Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken) =>
        executionStore.UpdateCreatedDirectoryStateAsync(
            jobId,
            leaseToken,
            path,
            state,
            cancellationToken);
}
