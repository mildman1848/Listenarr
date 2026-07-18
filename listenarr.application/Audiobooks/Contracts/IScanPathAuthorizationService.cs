using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public enum ScanPathAuthorizationFailure
{
    None,
    InvalidPath,
    ConfigurationUnavailable,
    NoConfiguredRoots,
    OutsideConfiguredRoots,
    IdentityUnavailable
}

public sealed record ScanPathAuthorizationResult(
    string? Path,
    PathIdentitySnapshot? Identity,
    ScanPathAuthorizationFailure Failure,
    string? Error)
{
    public bool IsAuthorized =>
        !string.IsNullOrWhiteSpace(Path)
        && Identity.HasValue
        && Failure == ScanPathAuthorizationFailure.None
        && string.IsNullOrWhiteSpace(Error);

    public static ScanPathAuthorizationResult Authorized(
        string path,
        PathIdentitySnapshot identity) =>
        new(
            path,
            identity,
            ScanPathAuthorizationFailure.None,
            null);

    public static ScanPathAuthorizationResult Rejected(
        ScanPathAuthorizationFailure failure,
        string error) =>
        new(null, null, failure, error);
}

public interface IScanPathAuthorizationService
{
    Task<ScanPathAuthorizationResult> AuthorizeAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<ScanPathAuthorizationResult> ResolveDefaultAsync(
        string? preferredPath,
        CancellationToken cancellationToken = default);
}
