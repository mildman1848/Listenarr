namespace Listenarr.Application.Common;

/// <summary>
/// Serializes filesystem check-and-mutate workflows within one Listenarr process.
/// Deployments must run a single Listenarr process per database; this in-memory gate
/// does not coordinate independent processes.
/// </summary>
public sealed class FilesystemMutationCoordinator : IFilesystemMutationCoordinator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<int> _depth = new();

    public Task ExecuteExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    public async Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        if (_depth.Value > 0)
        {
            _depth.Value++;
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
            }
        }

        await _gate.WaitAsync(cancellationToken);
        _depth.Value = 1;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _depth.Value = 0;
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
