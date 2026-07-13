namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookOperationCoordinator
{
    Task ExecuteExclusiveAsync(
        int audiobookId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteExclusiveAsync<T>(
        int audiobookId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
