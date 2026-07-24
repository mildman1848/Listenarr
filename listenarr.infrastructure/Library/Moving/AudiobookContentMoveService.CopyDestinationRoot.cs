using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task EnsureCopyDestinationRootAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string copyDestination,
        bool useTemp,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        faultInjector?.OnCopyMutation(
            request.JobId,
            CopyMutationFaultPoint.BeforeCopyRootValidation);
        if (Directory.Exists(copyDestination))
        {
            return;
        }

        if (useTemp)
        {
            throw new MoveNeedsAttentionException(
                "The validated move temporary directory disappeared before copying began.");
        }

        var normalizedDestination = Path.GetFullPath(copyDestination);
        if (!FileSystemPathIdentity.AreEquivalent(
                normalizedDestination,
                Path.GetFullPath(target),
                targetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "The direct copy destination does not match the validated move target.");
        }

        var parent = Path.GetDirectoryName(normalizedDestination)
            ?? throw new MoveNeedsAttentionException(
                "The direct copy destination has no parent directory.");
        var childName = Path.GetFileName(normalizedDestination);
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        ValidateMoveRootPath(
            normalizedDestination,
            mustExist: false,
            "copy destination");
        using var creation = PinnedDirectoryCreation.TryCreate(parent, childName);
        if (!creation.Created)
        {
            throw new MoveNeedsAttentionException(
                "The direct copy destination appeared before Listenarr could claim it exclusively.");
        }
        if (!creation.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The direct copy destination parent changed during exclusive creation.");
        }

        ValidateExistingMoveDirectory(
            normalizedDestination,
            "copy destination root");
    }
}
