using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<bool> PerformOwnedManualImportActionAsync(
        FileAction action,
        string source,
        string destination,
        Audiobook audiobook,
        IReadOnlyCollection<RootFolder> rootFolders,
        FileSystemPathSemantics semantics,
        string fallbackBoundary,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The manual import destination has no parent directory.");
        var boundary = LibraryDirectoryOwnershipPlanning.SelectMostSpecificBoundary(
            destinationDirectory,
            rootFolders.Select(root => root.Path),
            semantics);
        boundary ??= fallbackBoundary;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException(
                "The manual import destination has no managed ownership boundary.");
        }

        var operationId = FileMoveOperationIdentity.Create(
            "manual-import",
            audiobook.Id,
            action,
            Path.GetFullPath(source),
            Path.GetFullPath(destination));
        await _directoryOwnershipStore.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            boundary,
            semantics,
            "manual-import",
            operationId,
            audiobook.Id,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return await _fileMover.PerformActionOn(
            action,
            source,
            destination,
            operationId);
    }
}
