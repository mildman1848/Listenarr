using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static void MovePublicFileToPrivateClaim(
        string publicPath,
        string privateDirectory,
        string privateName)
    {
        var publicParentPath = Path.GetDirectoryName(Path.GetFullPath(publicPath))
            ?? throw new InvalidOperationException("The public file has no parent directory.");
        using var publicParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            publicParentPath);
        using var publicEntry = publicParent.OpenExistingFile(
            Path.GetFileName(publicPath),
            requireDeleteAccess: true);
        using var privateParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            privateDirectory);
        publicEntry.MoveTo(privateParent, privateName);
    }

    private static void PublishPrivateClaimNoReplace(
        string privatePath,
        string publicPath)
    {
        var privateParentPath = Path.GetDirectoryName(Path.GetFullPath(privatePath))
            ?? throw new InvalidOperationException("The private claim has no parent directory.");
        var publicParentPath = Path.GetDirectoryName(Path.GetFullPath(publicPath))
            ?? throw new InvalidOperationException("The public file has no parent directory.");
        using var privateParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            privateParentPath);
        using var privateEntry = privateParent.OpenExistingFile(
            Path.GetFileName(privatePath),
            requireDeleteAccess: true);
        using var publicParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            publicParentPath);
        privateEntry.MoveTo(publicParent, Path.GetFileName(publicPath));
    }

    private async Task PublishPreparedFileReplacingCapturedDestinationAsync(
        string preparedPath,
        string destinationPath)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);
        var destinationParent = Path.GetDirectoryName(normalizedDestination)
            ?? throw new InvalidOperationException(
                "The destination file has no parent directory.");
        var semantics = await _semanticsResolver.ResolveAsync(normalizedDestination);
        if (semantics.State != PathIdentityState.Valid
            || semantics.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new IOException(
                "Filesystem identity is unavailable for recoverable file publication.");
        }

        var publicationIdentity = FileSystemPathIdentity.CreateKey(
            "file-publication",
            normalizedDestination,
            semantics.Semantics);
        var stateDirectory = Path.Join(
            destinationParent,
            $".listenarr-file-publication-{HashPathIdentity(publicationIdentity)}.state");
        var preparedClaim = Path.Join(stateDirectory, "prepared.claim");
        var previousClaim = Path.Join(stateDirectory, "destination.previous");
        var publicationFence = Path.Join(stateDirectory, "publication.fence");
        RecoverPreparedFilePublication(
            stateDirectory,
            preparedClaim,
            previousClaim,
            publicationFence,
            normalizedDestination);
        CreatePrivateStateDirectory(stateDirectory);
        var published = false;
        try
        {
            if (File.Exists(normalizedDestination))
            {
                MovePublicFileToPrivateClaim(
                    normalizedDestination,
                    stateDirectory,
                    Path.GetFileName(previousClaim));
                if (AfterPreparedDestinationCapturedForTestAsync != null)
                {
                    await AfterPreparedDestinationCapturedForTestAsync();
                }
            }

            MovePublicFileToPrivateClaim(
                preparedPath,
                stateDirectory,
                Path.GetFileName(preparedClaim));
            WriteGenerationFence(publicationFence);
            PublishPrivateClaimNoReplace(preparedClaim, normalizedDestination);
            published = true;
            TryDeleteFile(previousClaim);
            TryDeleteFile(publicationFence);
            TryDeleteEmptyStateDirectory(stateDirectory);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            if (!published)
            {
                TryRestoreStateFile(preparedClaim, preparedPath);
                TryRestoreStateFile(previousClaim, normalizedDestination);
                TryDeleteFile(publicationFence);
                TryDeleteEmptyStateDirectory(stateDirectory);
            }
            throw;
        }
    }

    private void RecoverPreparedFilePublication(
        string stateDirectory,
        string preparedClaim,
        string previousClaim,
        string publicationFence,
        string destinationPath)
    {
        if (!Directory.Exists(stateDirectory))
        {
            return;
        }
        if (!TryValidateStateDirectory(stateDirectory)
            || !StateDirectoryContainsOnly(
                stateDirectory,
                preparedClaim,
                previousClaim,
                publicationFence))
        {
            throw new IOException(
                "Recoverable file-publication state contains unsafe entries.");
        }

        var preparedExists = File.Exists(preparedClaim);
        var previousExists = File.Exists(previousClaim);
        var publicationFenceExists = File.Exists(publicationFence);
        var destinationExists = File.Exists(destinationPath);
        if ((preparedExists && IsLinkedOrUnverifiableEntry(preparedClaim))
            || (previousExists && IsLinkedOrUnverifiableEntry(previousClaim))
            || (publicationFenceExists
                && IsLinkedOrUnverifiableEntry(publicationFence))
            || (destinationExists && IsLinkedOrUnverifiableEntry(destinationPath)))
        {
            throw new IOException(
                "Recoverable file-publication state contains an unverifiable entry.");
        }

        if (!publicationFenceExists)
        {
            if (preparedExists
                || (destinationExists && previousExists))
            {
                throw new IOException(
                    "Interrupted file publication has ambiguous pre-commit state.");
            }

            if (previousExists)
            {
                TryRestoreStateFile(previousClaim, destinationPath);
                if (File.Exists(previousClaim))
                {
                    throw new IOException(
                        "The captured destination generation could not be restored.");
                }
            }

            TryDeleteEmptyStateDirectory(stateDirectory);
            return;
        }

        if (destinationExists)
        {
            if (preparedExists)
            {
                throw new IOException(
                    "The destination was recreated before interrupted publication could recover.");
            }

            // The prepared claim was atomically renamed to the destination before
            // the process stopped. Retire only the captured previous generation.
            TryDeleteFile(previousClaim);
            TryDeleteFile(publicationFence);
            TryDeleteEmptyStateDirectory(stateDirectory);
            return;
        }

        if (preparedExists)
        {
            PublishPrivateClaimNoReplace(preparedClaim, destinationPath);
            TryDeleteFile(previousClaim);
            TryDeleteFile(publicationFence);
            TryDeleteEmptyStateDirectory(stateDirectory);
            return;
        }

        if (previousExists)
        {
            TryRestoreStateFile(previousClaim, destinationPath);
            if (File.Exists(previousClaim))
            {
                throw new IOException(
                    "The captured destination generation could not be restored.");
            }
        }

        TryDeleteFile(publicationFence);
        TryDeleteEmptyStateDirectory(stateDirectory);
    }
}
