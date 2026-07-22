namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private void PublishTargetScaffoldingForTestableBoundary(
        Guid jobId,
        string temporaryRoot,
        string publishedRoot)
    {
        faultInjector?.OnTargetScaffoldPreparation(
            jobId,
            TargetScaffoldPreparationFaultPoint.BeforePublication);
        Directory.Move(temporaryRoot, publishedRoot);
        faultInjector?.OnTargetScaffoldPreparation(
            jobId,
            TargetScaffoldPreparationFaultPoint.AfterPublication);
    }
}
