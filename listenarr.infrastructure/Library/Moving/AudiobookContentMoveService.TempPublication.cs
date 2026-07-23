namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task PublishOwnedTempDirectoryAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string tempName,
        string targetParent,
        CancellationToken cancellationToken)
    {
        using var tempPublication = PinnedDirectoryCreation.OpenExistingForPublication(
            targetParent,
            Path.GetFileName(tempName));
        if (!tempPublication.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory changed before publication.");
        }

        await ValidateOwnedTempDirectoryAsync(
            tempName,
            targetParent,
            request,
            source,
            target,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        if (Directory.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "The move target appeared before temporary publication.");
        }

        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        faultInjector?.OnTempPublication(
            request.JobId,
            TempPublicationFaultPoint.BeforeFinalValidation);
        await ValidateOwnedTempDirectoryAsync(
            tempName,
            targetParent,
            request,
            source,
            target,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        if (Directory.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "The move target appeared immediately before temporary publication.");
        }

        faultInjector?.OnTempPublication(
            request.JobId,
            TempPublicationFaultPoint.BeforePublication);
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        if (!tempPublication.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory or its parent changed at publication.");
        }
        ValidateMoveTargetRoot(target);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new MoveNeedsAttentionException(
                "The move target appeared at the final temporary publication boundary.");
        }

        using var publishedTemp = tempPublication.PublishCreatedDirectoryAs(
            Path.GetFileName(target));
        if (!publishedTemp.VisiblePathMatches(target))
        {
            throw new MoveNeedsAttentionException(
                "The published move target does not identify the validated temporary directory.");
        }
    }
}
