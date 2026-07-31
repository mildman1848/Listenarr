using System.Data.Common;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    private static void EnsureEquivalentIdentity(
        string persisted,
        string current,
        FileSystemPathSemantics semantics,
        string mismatchMessage,
        string invalidMessage)
    {
        try
        {
            if (!FileSystemPathIdentity.AreEquivalent(persisted, current, semantics))
            {
                throw new MoveNeedsAttentionException(mismatchMessage);
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(invalidMessage);
        }
    }

    private static async Task<bool> IsLeaseActiveAsync(
        ListenArrDbContext db,
        Guid jobId,
        MoveLeaseToken leaseToken,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await db.MoveJobs.AnyAsync(
            job => job.Id == jobId
                && job.Status == MoveJobStatus.Running
                && job.LeaseOwner == leaseToken.Owner
                && job.LeaseGeneration == leaseToken.Generation
                && job.LeaseExpiresAt != null
                && job.LeaseExpiresAt > nowUtc,
            cancellationToken);

    private static void EnsureLeaseTokenProvided(
        Guid jobId,
        MoveLeaseToken leaseToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken.Owner)
            || leaseToken.Generation <= 0)
        {
            throw new MoveLeaseLostException(jobId, leaseToken.Generation);
        }
    }

    private static async Task ExecuteAsync(
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            ShouldTranslate(exception, cancellationToken))
        {
            throw new PersistenceException($"Failed to {operation}.", exception);
        }
    }

    private static async Task<T> ExecuteAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (
            ShouldTranslate(exception, cancellationToken))
        {
            throw new PersistenceException($"Failed to {operation}.", exception);
        }
    }

    private static bool ShouldTranslate(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is PersistenceException
            or MoveLeaseLostException
            or MoveNeedsAttentionException)
        {
            return false;
        }

        if (exception is OperationCanceledException
            && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ContainsProviderFailure(exception);
    }

    private static bool ContainsProviderFailure(Exception exception)
    {
        if (exception is DbException
            or DbUpdateException
            or DbUpdateConcurrencyException)
        {
            return true;
        }

        return exception.InnerException != null
            && ContainsProviderFailure(exception.InnerException);
    }
}
