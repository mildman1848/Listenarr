namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    public Task<List<ImportResult>> ImportDownloadFilesAsync(
        Audiobook audiobook,
        List<string> files,
        CancellationToken ct = default,
        DownloadImportOptions? options = null) =>
        audiobookOperationCoordinator != null
            ? audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                token => ImportDownloadFilesCoreAsync(audiobook, files, token, options),
                ct)
            : ImportDownloadFilesCoreAsync(audiobook, files, ct, options);
}
