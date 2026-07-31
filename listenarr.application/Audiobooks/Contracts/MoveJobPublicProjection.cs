namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveJobPublicUpdate(
    string JobId,
    int? AudiobookId,
    string Status,
    string? Error,
    string? Target,
    DateTime UpdatedAt);

public static class MoveJobPublicProjection
{
    public static string? ToError(MoveJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ToError(job.Error, job.FailureKind);
    }

    public static string? ToError(
        string? internalError,
        MoveFailureKind failureKind)
    {
        if (string.IsNullOrWhiteSpace(internalError))
        {
            return null;
        }

        return failureKind switch
        {
            MoveFailureKind.Transient =>
                "The move was interrupted and can be retried.",
            MoveFailureKind.SourceDrift =>
                "The source changed during the move. Review the source and retry.",
            MoveFailureKind.Verification =>
                "The moved files could not be verified.",
            MoveFailureKind.UnsupportedEntry =>
                "The source contains an unsupported filesystem entry.",
            MoveFailureKind.Persistence =>
                "The move state could not be saved.",
            MoveFailureKind.Unknown or MoveFailureKind.None =>
                "The move failed. Review the server logs for details.",
            _ => "The move failed. Review the server logs for details."
        };
    }

    public static MoveJobPublicUpdate CreateUpdate(
        Guid jobId,
        MoveJobStatus fallbackStatus,
        string? fallbackError,
        DateTime fallbackUpdatedAt,
        MoveJob? persistedJob)
    {
        var status = persistedJob?.Status ?? fallbackStatus;
        var error = persistedJob == null
            ? ToError(fallbackError, MoveFailureKind.Unknown)
            : ToError(persistedJob);
        return new MoveJobPublicUpdate(
            jobId.ToString(),
            persistedJob?.AudiobookId,
            status.ToString(),
            error,
            persistedJob?.RequestedPath,
            persistedJob?.UpdatedAt ?? fallbackUpdatedAt);
    }
}
