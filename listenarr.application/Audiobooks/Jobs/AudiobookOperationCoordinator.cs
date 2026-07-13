namespace Listenarr.Application.Audiobooks.Jobs;

public sealed class AudiobookOperationCoordinator : IAudiobookOperationCoordinator, IDisposable
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References;
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly AsyncLocal<Dictionary<int, int>?> _held = new();
    private bool _disposed;

    public Task ExecuteExclusiveAsync(
        int audiobookId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync<object?>(
            audiobookId,
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    public async Task<T> ExecuteExclusiveAsync<T>(
        int audiobookId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var held = _held.Value;
        if (held != null && held.TryGetValue(audiobookId, out var depth))
        {
            held[audiobookId] = depth + 1;
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                held[audiobookId] = depth;
            }
        }

        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(audiobookId, out entry!))
            {
                entry = new Entry();
                _entries.Add(audiobookId, entry);
            }
            entry.References++;
        }

        var acquired = false;
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
            acquired = true;
            held ??= [];
            _held.Value = held;
            held[audiobookId] = 1;
            return await operation(cancellationToken);
        }
        finally
        {
            if (acquired)
            {
                held!.Remove(audiobookId);
                entry.Gate.Release();
            }

            lock (_sync)
            {
                entry.References--;
                if (entry.References == 0
                    && _entries.TryGetValue(audiobookId, out var current)
                    && ReferenceEquals(current, entry))
                {
                    _entries.Remove(audiobookId);
                    entry.Gate.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pair in _entries
                .Where(pair => pair.Value.References == 0)
                .ToList())
            {
                pair.Value.Gate.Dispose();
                _entries.Remove(pair.Key);
            }
        }
    }
}
