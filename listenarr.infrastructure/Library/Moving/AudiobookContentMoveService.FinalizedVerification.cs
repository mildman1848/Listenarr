namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task VerifyFinalizedMoveAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = await WithValidatedTargetDirectoryOwnershipAsync(
            request,
            cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);

        ValidateMoveRootPath(source, mustExist: false, "source recovery");
        ValidateMoveTargetRoot(target);
        if (!Directory.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "The finalized move target no longer exists.");
        }

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "A markerless finalized move cannot be verified without a persisted manifest.");
        }

        faultInjector?.OnFinalizedVerification(
            request.JobId,
            FinalizedVerificationFaultPoint.BeforeManifestVerification);
        ValidateTargetManifest(target, manifest, request.TargetSemantics);
        var tempOwnership = await TryValidatePublishedTempOwnershipAsync(
            target,
            request,
            source,
            target,
            cancellationToken);
        var quarantineOwnership = await TryValidateExistingQuarantineDirectoryAsync(
            source,
            target,
            request.JobId,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);
        ValidateExistingDestinationContents(
            source,
            target,
            manifest,
            request.JobId,
            request.TargetSemantics,
            tempOwnership,
            quarantineOwnership,
            allowPartialFiles: false,
            targetDirectoryOwnership: request.TargetDirectoryOwnership);
        await VerifyPublishedManifestAsync(
            target,
            manifest,
            request.TargetSemantics,
            cancellationToken);

        VerifySourceCleanupState(request, source, target);
    }
}
