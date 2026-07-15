namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    public Task<List<ImportResult>> ImportDownloadFilesAsync(
        Audiobook audiobook,
        List<string> files,
        CancellationToken ct = default,
        DownloadImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        return audiobookOperationCoordinator.ExecuteExclusiveAsync(
            audiobook.Id,
            async token =>
            {
                var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(
                    audiobook.Id,
                    token)
                    ?? throw new InvalidOperationException(
                        $"Audiobook {audiobook.Id} no longer exists");
                return await ImportDownloadFilesCoreAsync(
                    currentAudiobook,
                    files,
                    token,
                    options);
            },
            ct);
    }
}
