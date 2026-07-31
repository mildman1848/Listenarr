namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private Task<bool> RegisterPublishedManualImportAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        CancellationToken cancellationToken)
    {
        return _audiobookFileService.RegisterPublishedGenerationAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            "manual-import",
            cancellationToken);
    }
}
