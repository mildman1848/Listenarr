using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task<T> ExecuteWithAllAudiobookLocksAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var audiobookIds = await db.Audiobooks
            .AsNoTracking()
            .OrderBy(audiobook => audiobook.Id)
            .Select(audiobook => audiobook.Id)
            .ToListAsync(cancellationToken);

        return await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
            audiobookIds,
            operation,
            cancellationToken);
    }
}
