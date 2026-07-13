namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs;

public sealed class AudiobookOperationCoordinatorTests
{
    [Fact]
    public async Task SameAudiobookOperations_AreSerialized()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;
        var second = coordinator.ExecuteExclusiveAsync(
            42,
            _ =>
            {
                secondEntered = true;
                return Task.CompletedTask;
            });

        await Task.Delay(50);
        Assert.False(secondEntered);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task DifferentAudiobooks_CanRunConcurrently()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;
        var second = coordinator.ExecuteExclusiveAsync(
            43,
            _ =>
            {
                secondEntered.TrySetResult();
                return Task.CompletedTask;
            });

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task NestedSameAudiobookOperations_RemainReentrantUntilOuterScopeExits()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var nestedCount = 0;

        await coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                await coordinator.ExecuteExclusiveAsync(
                    42,
                    _ =>
                    {
                        nestedCount++;
                        return Task.CompletedTask;
                    });
                await coordinator.ExecuteExclusiveAsync(
                    42,
                    _ =>
                    {
                        nestedCount++;
                        return Task.CompletedTask;
                    });
            });

        Assert.Equal(2, nestedCount);
    }

    [Fact]
    public async Task AlreadyCanceledToken_ThrowsOperationCanceledExceptionBeforeQueueing()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ExecuteExclusiveAsync(
                42,
                _ => Task.CompletedTask,
                cancellation.Token));
    }

    [Fact]
    public async Task CanceledWaiter_ReleasesItsEntryReference()
    {
        using var coordinator = new AudiobookOperationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteExclusiveAsync(
            42,
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;

        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.ExecuteExclusiveAsync(
            42,
            _ => Task.CompletedTask,
            cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        releaseFirst.TrySetResult();
        await first;

        var entriesField = typeof(AudiobookOperationCoordinator).GetField(
            "_entries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var entries = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            entriesField!.GetValue(coordinator));
        Assert.Empty(entries);
    }
}
