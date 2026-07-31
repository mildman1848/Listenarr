using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal interface IMoveExecutionStore
{
    Task EnsureLeaseOwnedAsync(Guid jobId, MoveLeaseToken leaseToken, CancellationToken cancellationToken);

    Task ValidateOrAdoptIdentityAsync(
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        bool hasFilesystemRecoveryArtifacts,
        CancellationToken cancellationToken);

    Task EnsureMutationAuthorizedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken);

    Task<List<MoveJobEntry>> LoadManifestAsync(Guid jobId, CancellationToken cancellationToken);

    Task UpdateCleanupStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        MoveJobEntryCleanupState cleanupState,
        CancellationToken cancellationToken);

    Task UpdateCleanupProtectionVersionAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        int cleanupProtectionVersion,
        CancellationToken cancellationToken);

    Task UpdateCopyStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken);

    Task UpdateJobPhaseAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        MoveJobPhase phase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken);

    Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken);

    Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken);
}
