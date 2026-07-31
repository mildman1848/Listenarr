using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<string> GetPreparedFilePublicationStateNameAsync(
        string destinationPath)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);
        var semantics = await _semanticsResolver.ResolveAsync(normalizedDestination);
        if (semantics.State != PathIdentityState.Valid
            || semantics.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Unknown)
        {
            throw new IOException(
                "Filesystem identity is unavailable for recoverable file publication.");
        }

        var publicationIdentity = FileSystemPathIdentity.CreateKey(
            "file-publication",
            normalizedDestination,
            semantics.Semantics);
        return $".listenarr-file-publication-{HashPathIdentity(publicationIdentity)}.state";
    }

    private void RecoverPreparedFilePublication(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string destinationName,
        string stateName)
    {
        using var statePublication =
            destinationParent.TryOpenExistingChildForPublication(stateName);
        if (statePublication == null)
        {
            return;
        }

        using var state = statePublication.OpenCreatedDirectoryAnchor();
        if (!destinationParent.VisiblePathMatches()
            || !state.VisiblePathMatches()
            || !AnchoredStateContainsOnly(
                state,
                "prepared.claim",
                "destination.previous",
                "publication.fence"))
        {
            throw new IOException(
                "Recoverable file-publication state contains unsafe entries.");
        }

        var prepared = state.TryOpenExistingFile(
            "prepared.claim",
            requireDeleteAccess: true);
        var previous = state.TryOpenExistingFile(
            "destination.previous",
            requireDeleteAccess: true);
        var fence = state.TryOpenExistingFile(
            "publication.fence",
            requireDeleteAccess: true);
        try
        {
            using var destination = destinationParent.TryOpenExistingFile(
                destinationName,
                requireDeleteAccess: false);
            if (fence == null)
            {
                if (destination != null && previous != null)
                {
                    throw new IOException(
                        "Interrupted file publication has ambiguous pre-commit state.");
                }

                if (prepared != null)
                {
                    if (destination != null || previous == null)
                    {
                        throw new IOException(
                            "Interrupted file publication has incomplete pre-commit evidence.");
                    }

                    prepared.Delete(immediateWindows: true);
                    prepared.Dispose();
                    prepared = null;
                    FlushFileMoveDirectory(
                        state,
                        "uncommitted prepared-generation retirement");
                }

                if (previous != null)
                {
                    previous.MoveTo(destinationParent, destinationName);
                    previous.Dispose();
                    previous = null;
                    FlushFileMoveDirectory(
                        destinationParent,
                        "interrupted destination restoration");
                    FlushFileMoveDirectory(
                        state,
                        "interrupted previous-generation retirement");
                }
            }
            else if (destination != null)
            {
                if (prepared != null)
                {
                    throw new IOException(
                        "The destination was recreated before interrupted publication could recover.");
                }

                previous?.Delete(immediateWindows: true);
                previous?.Dispose();
                previous = null;
                fence.Delete(immediateWindows: true);
                fence.Dispose();
                fence = null;
                FlushFileMoveDirectory(
                    state,
                    "completed publication-journal retirement");
            }
            else if (prepared != null)
            {
                prepared.MoveTo(destinationParent, destinationName);
                prepared.Dispose();
                prepared = null;
                FlushFileMoveDirectory(
                    destinationParent,
                    "interrupted prepared-generation publication");
                FlushFileMoveDirectory(
                    state,
                    "interrupted prepared-claim retirement");
                previous?.Delete(immediateWindows: true);
                previous?.Dispose();
                previous = null;
                fence.Delete(immediateWindows: true);
                fence.Dispose();
                fence = null;
                FlushFileMoveDirectory(
                    state,
                    "recovered publication-journal retirement");
            }
            else
            {
                if (previous != null)
                {
                    previous.MoveTo(destinationParent, destinationName);
                    previous.Dispose();
                    previous = null;
                    FlushFileMoveDirectory(
                        destinationParent,
                        "interrupted previous-generation restoration");
                    FlushFileMoveDirectory(
                        state,
                        "interrupted previous-generation retirement");
                }

                fence.Delete(immediateWindows: true);
                fence.Dispose();
                fence = null;
                FlushFileMoveDirectory(
                    state,
                    "abandoned publication-journal retirement");
            }
        }
        finally
        {
            fence?.Dispose();
            previous?.Dispose();
            prepared?.Dispose();
        }

        state.Dispose();
        statePublication.DeletePinnedEmptyDirectory(
            stateName,
            immediateWindows: true);
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state retirement");
    }

    private async Task PublishPreparedFileReplacingCapturedDestinationAsync(
        PinnedDirectoryCreation.PinnedFileEntry prepared,
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string destinationName,
        PinnedDirectoryCreation.PinnedFileEntry capturedDestination,
        string stateName)
    {
        using var statePublication = CreateAnchoredFileMoveStateDirectory(
            destinationParent,
            stateName);
        using var state = statePublication.OpenCreatedDirectoryAnchor();
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state creation");

        capturedDestination.MoveTo(state, "destination.previous");
        FlushFileMoveDirectory(
            destinationParent,
            "captured destination retirement");
        FlushFileMoveDirectory(
            state,
            "captured destination publication");
        if (AfterPreparedDestinationCapturedForTestAsync != null)
        {
            await AfterPreparedDestinationCapturedForTestAsync();
        }

        prepared.MoveTo(state, "prepared.claim");
        FlushFileMoveDirectory(
            destinationParent,
            "prepared generation retirement");
        FlushFileMoveDirectory(
            state,
            "prepared generation claim");

        using var fence = state.CreateNewFile("publication.fence");
        fence.FlushToDisk();
        FlushFileMoveDirectory(state, "file-publication commit fence");

        using var appearedDestination = destinationParent.TryOpenExistingFile(
            destinationName,
            requireDeleteAccess: false);
        if (appearedDestination != null)
        {
            throw new IOException(
                "The destination was recreated after its captured generation was quarantined.");
        }

        prepared.MoveTo(destinationParent, destinationName);
        FlushFileMoveDirectory(
            destinationParent,
            "prepared generation publication");
        FlushFileMoveDirectory(
            state,
            "prepared generation claim retirement");

        capturedDestination.Delete(immediateWindows: true);
        capturedDestination.Dispose();
        fence.Delete(immediateWindows: true);
        fence.Dispose();
        FlushFileMoveDirectory(state, "file-publication journal retirement");

        state.Dispose();
        statePublication.DeletePinnedEmptyDirectory(
            stateName,
            immediateWindows: true);
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state retirement");
    }
}
