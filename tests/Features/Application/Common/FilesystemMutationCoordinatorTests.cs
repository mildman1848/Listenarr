namespace Listenarr.Tests.Features.Application.Common;

public sealed class FilesystemMutationCoordinatorTests
{
    [Fact]
    public async Task ExecuteExclusiveAsync_AllowsOnlyOneOperationAtATime()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;

        var second = coordinator.ExecuteExclusiveAsync(_ =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_NestedCallIsReentrantWhileExternalCallerWaits()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var nestedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOuter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var externalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outer = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            await coordinator.ExecuteExclusiveAsync(_ =>
            {
                nestedEntered.SetResult();
                return Task.CompletedTask;
            });
            await releaseOuter.Task;
        });
        await nestedEntered.Task;

        var external = coordinator.ExecuteExclusiveAsync(_ =>
        {
            externalEntered.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        Assert.False(externalEntered.Task.IsCompleted);
        releaseOuter.SetResult();
        await Task.WhenAll(outer, external);
        Assert.True(externalEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_CancelledWaiterDoesNotEnter()
    {
        using var coordinator = new FilesystemMutationCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEntered = false;
        var first = coordinator.ExecuteExclusiveAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = coordinator.ExecuteExclusiveAsync(_ =>
        {
            waiterEntered = true;
            return Task.CompletedTask;
        }, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(waiterEntered);
        releaseFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_ReleasesGateAfterException()
    {
        using var coordinator = new FilesystemMutationCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteExclusiveAsync(_ =>
                throw new InvalidOperationException("expected")));

        var entered = false;
        await coordinator.ExecuteExclusiveAsync(_ =>
        {
            entered = true;
            return Task.CompletedTask;
        });
        Assert.True(entered);
    }

    [Fact]
    public async Task ExecuteExclusiveAsync_ReleasesGateAfterOperationCancellation()
    {
        using var coordinator = new FilesystemMutationCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteExclusiveAsync(_ => Task.FromCanceled(new CancellationToken(true))));

        var result = await coordinator.ExecuteExclusiveAsync(_ => Task.FromResult(42));
        Assert.Equal(42, result);
    }
}
