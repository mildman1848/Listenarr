using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<bool> PerformOwnedFileActionAsync(
        FileAction action,
        string source,
        string destination,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        Guid operationId,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The import destination has no parent directory.");
        await directoryOwnershipStore.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            managedBoundary,
            semantics,
            "download-import",
            operationId,
            audiobookId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await fileMover.PerformActionOn(action, source, destination);
    }
}
