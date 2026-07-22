namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private void InvokeBeforeTargetScaffoldPublication(Guid jobId) =>
        faultInjector?.OnTargetScaffoldPreparation(
            jobId,
            TargetScaffoldPreparationFaultPoint.BeforePublication);
}
