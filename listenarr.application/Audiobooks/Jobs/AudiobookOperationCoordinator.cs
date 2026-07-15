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
        ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

    public Task<T> ExecuteExclusiveAsync<T>(
        int audiobookId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

    public Task ExecuteExclusiveAsync(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync<object?>(
            audiobookIds,
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    public async Task<T> ExecuteExclusiveAsync<T>(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobookIds);
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedIds = audiobookIds
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (orderedIds.Length == 0)
        {
            return await operation(cancellationToken);
        }

        var held = _held.Value;
        if (held is { Count: > 0 })
        {
            var highestHeldId = held.Keys.Max();
            var lowerUnheldId = orderedIds
                .Where(id => !held.ContainsKey(id) && id < highestHeldId)
                .Select(id => (int?)id)
                .FirstOrDefault();
            if (lowerUnheldId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Cannot acquire audiobook operation key {lowerUnheldId.Value} after higher key {highestHeldId} is already held.");
            }
        }

        var acquired = new List<(int Id, Entry Entry)>();
        var reentered = new List<(int Id, int PreviousDepth)>();
        try
        {
            foreach (var audiobookId in orderedIds)
            {
                if (held != null && held.TryGetValue(audiobookId, out var depth))
                {
                    held[audiobookId] = depth + 1;
                    reentered.Add((audiobookId, depth));
                    continue;
                }

                var entry = AddReference(audiobookId);
                try
                {
                    await entry.Gate.WaitAsync(cancellationToken);
                }
                catch
                {
                    ReleaseReference(audiobookId, entry, releaseGate: false);
                    throw;
                }

                acquired.Add((audiobookId, entry));
                held ??= [];
                _held.Value = held;
                held[audiobookId] = 1;
            }

            return await operation(cancellationToken);
        }
        finally
        {
            for (var index = reentered.Count - 1; index >= 0; index--)
            {
                var (audiobookId, previousDepth) = reentered[index];
                held![audiobookId] = previousDepth;
            }

            for (var index = acquired.Count - 1; index >= 0; index--)
            {
                var (audiobookId, entry) = acquired[index];
                held!.Remove(audiobookId);
                ReleaseReference(audiobookId, entry, releaseGate: true);
            }

            if (held is { Count: 0 })
            {
                _held.Value = null;
            }
        }
    }

    private Entry AddReference(int audiobookId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(audiobookId, out var entry))
            {
                entry = new Entry();
                _entries.Add(audiobookId, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void ReleaseReference(
        int audiobookId,
        Entry entry,
        bool releaseGate)
    {
        if (releaseGate)
        {
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
