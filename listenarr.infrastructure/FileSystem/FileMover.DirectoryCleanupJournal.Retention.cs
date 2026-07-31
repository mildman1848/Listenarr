namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static bool IsDirectChildOfCleanupDirectory(
        string relativeFile,
        string relativeDirectory)
    {
        var parent = Path.GetDirectoryName(relativeFile) ?? string.Empty;
        return string.Equals(
            parent,
            relativeDirectory,
            StringComparison.Ordinal);
    }

    private static string GetCleanupRecoveryRelativePath(
        CleanupJournalPayload payload,
        CleanupJournalFile file)
    {
        var directory = Path.GetDirectoryName(file.RelativePath);
        var recoveryName =
            PinnedDestinationRetentionGuard.CreateSourceRecoveryName(
                payload.OperationId,
                file.RelativePath);
        return string.IsNullOrWhiteSpace(directory)
            ? recoveryName
            : Path.Join(directory, recoveryName);
    }

    private static async Task<PinnedDestinationRetentionGuard?>
        OpenExistingCleanupRetentionAsync(
            CleanupJournalPayload payload,
            CleanupJournalFile file)
    {
        try
        {
            using var destinationRoot =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    payload.DestinationRoot,
                    createMissing: false);
            using var destinationRootHandle =
                destinationRoot.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(
                    destinationRootHandle,
                    out var destinationRootIdentity)
                || destinationRootIdentity != payload.DestinationRootIdentity)
            {
                return null;
            }

            using var destinationParent = OpenRelativeCleanupDirectory(
                destinationRoot,
                Path.GetDirectoryName(file.RelativePath));
            var retentionName =
                PinnedDestinationRetentionGuard.CreateRetentionName(
                    payload.OperationId,
                    file.RelativePath);
            return await PinnedDestinationRetentionGuard.OpenExistingAsync(
                destinationParent,
                Path.GetFileName(file.RelativePath),
                retentionName,
                file.Length,
                file.Sha256,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenRelativeCleanupDirectory(
            PinnedDirectoryCreation.PinnedDirectoryAnchor root,
            string? relativeDirectory)
    {
        var current = root.Duplicate();
        try
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory)
                || relativeDirectory == ".")
            {
                return current;
            }

            foreach (var segment in relativeDirectory.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "A cleanup destination path contains navigation segments.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
