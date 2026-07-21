using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public static class MoveJobBoundaryConflict
{
    public static bool TouchesBoundary(
        MoveJob job,
        string boundaryPath,
        FileSystemPathSemantics boundarySemantics)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        if (!job.Status.IsActive())
        {
            return false;
        }

        return EndpointTouchesBoundary(
                job.SourcePath,
                job.TryGetSourceIdentity(out var sourceIdentity)
                    ? sourceIdentity
                    : null,
                boundaryPath,
                boundarySemantics)
            || EndpointTouchesBoundary(
                job.RequestedPath,
                job.TryGetTargetIdentity(out var targetIdentity)
                    ? targetIdentity
                    : null,
                boundaryPath,
                boundarySemantics);
    }

    public static bool EndpointTouchesBoundary(
        string? endpointPath,
        PathIdentitySnapshot? endpointIdentity,
        string boundaryPath,
        FileSystemPathSemantics boundarySemantics)
    {
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            return false;
        }

        try
        {
            var endpointSemantics = endpointIdentity?.Semantics ?? boundarySemantics;
            if (endpointIdentity.HasValue)
            {
                endpointIdentity.Value.ValidateForPath(endpointPath);
            }

            return FileSystemPathIdentity.EvaluateBoundaryConflict(
                    endpointPath,
                    endpointSemantics,
                    boundaryPath,
                    boundarySemantics)
                != FileSystemPathBoundaryConflict.None;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException)
        {
            // Active jobs with malformed or incomplete endpoint identity must block a
            // destructive root mutation until the job is reconciled or repaired.
            return true;
        }
    }
}
