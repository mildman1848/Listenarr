using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    public Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<MoveJobCreatedDirectory>>(
            "load move-created target directories",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await db.MoveJobCreatedDirectories
                    .AsNoTracking()
                    .Where(directory => directory.MoveJobId == jobId)
                    .OrderBy(directory => directory.Id)
                    .ToListAsync(cancellationToken);
            },
            cancellationToken);

    public Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move-created target directories",
            async () =>
            {
                if (paths.Count == 0)
                {
                    return;
                }

                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;
                if (!await IsLeaseActiveAsync(db, jobId, leaseToken, nowUtc, cancellationToken))
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var existing = await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == jobId)
                    .Select(directory => directory.Path)
                    .ToListAsync(cancellationToken);
                foreach (var path in paths.Except(existing, StringComparer.Ordinal))
                {
                    db.MoveJobCreatedDirectories.Add(new MoveJobCreatedDirectory
                    {
                        MoveJobId = jobId,
                        Path = path,
                        State = MoveCreatedDirectoryState.Planned
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            },
            cancellationToken);

    public Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move-created directory state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (!db.Database.IsRelational())
                {
                    var directory = await db.MoveJobCreatedDirectories.SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.Path == path
                            && candidate.MoveJob.Status == MoveJobStatus.Running
                            && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                            && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                            && candidate.MoveJob.LeaseExpiresAt != null
                            && candidate.MoveJob.LeaseExpiresAt > nowUtc,
                        cancellationToken);
                    if (directory == null)
                    {
                        throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                    }

                    directory.State = state;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var affected = await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == jobId
                        && directory.Path == path
                        && directory.MoveJob.Status == MoveJobStatus.Running
                        && directory.MoveJob.LeaseOwner == leaseToken.Owner
                        && directory.MoveJob.LeaseGeneration == leaseToken.Generation
                        && directory.MoveJob.LeaseExpiresAt != null
                        && directory.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(directory => directory.State, state),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);
}
