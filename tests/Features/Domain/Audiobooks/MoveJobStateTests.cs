namespace Listenarr.Tests.Features.Domain.Audiobooks;

public sealed class MoveJobStateTests
{
    [Fact]
    public void NewJob_HasDurableQueuedState()
    {
        var job = new MoveJob();

        Assert.Equal(MoveJobStatus.Queued, job.Status);
        Assert.Equal(MoveJobPhase.None, job.Phase);
        Assert.Equal(3, job.IdentityKeyVersion);
        Assert.Empty(job.Entries);
    }

    [Fact]
    public void ActiveAndTerminalStatuses_AreExplicit()
    {
        Assert.True(MoveJobStatus.Queued.IsActive());
        Assert.True(MoveJobStatus.RetryScheduled.IsActive());
        Assert.False(MoveJobStatus.NeedsAttention.IsActive());
        Assert.False(MoveJobStatus.Completed.IsActive());
        Assert.False(MoveJobStatus.Superseded.IsActive());
    }
}
