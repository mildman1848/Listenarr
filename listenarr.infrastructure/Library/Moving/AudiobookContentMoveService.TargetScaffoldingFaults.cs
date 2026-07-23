namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor>
        PublishTargetScaffoldingForTestableBoundaryAsync(
            Guid jobId,
            PreparedTargetScaffolding preparedScaffolding,
            string finalName,
            Func<Task> authorizeMutation)
    {
        faultInjector?.OnTargetScaffoldPreparation(
            jobId,
            TargetScaffoldPreparationFaultPoint.BeforePublication);
        await authorizeMutation();
        var publishedAnchor = preparedScaffolding.PublishAs(finalName);
        try
        {
            faultInjector?.OnTargetScaffoldPreparation(
                jobId,
                TargetScaffoldPreparationFaultPoint.AfterPublication);
            return publishedAnchor;
        }
        catch
        {
            publishedAnchor.Dispose();
            throw;
        }
    }
}
