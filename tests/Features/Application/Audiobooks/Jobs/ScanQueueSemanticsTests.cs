/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs
{
    [Trait("Area", "Jobs")]
    [Trait("Name", "ScanQueueSemanticsTests")]
    public sealed class ScanQueueSemanticsTests
    {
        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        public async Task ScanQueue_DedupeUsesResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            bool shouldDedupe)
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(caseSensitivity));
            var audiobook = new AudiobookBuilder()
                .WithId(1001)
                .WithTitle("Case Book")
                .Build();
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-scan-queue"));
            var first = Path.Join(root, "CaseBook");
            var second = Path.Join(root, "casebook");

            var firstJob = await queue.EnqueueScanAsync(audiobook, first);
            var secondJob = await queue.EnqueueScanAsync(audiobook, second);

            Assert.Equal(shouldDedupe, firstJob == secondJob);
        }

        [Fact]
        public async Task ScanQueue_CompletedCorrelationCreatesReplacementHandoff()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive));
            var audiobook = new AudiobookBuilder()
                .WithId(1002)
                .WithTitle("Move Completion Book")
                .Build();
            const string correlationId = "move:abc123";

            var earlierExplicitScan = await queue.EnqueueScanAsync(audiobook);
            queue.UpdateJobStatus(earlierExplicitScan, "Completed");
            var correlatedScan = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            queue.UpdateJobStatus(correlatedScan, "Completed");
            var replayedJob = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            var explicitRescan = await queue.EnqueueScanAsync(audiobook);

            Assert.NotEqual(correlatedScan, replayedJob);
            Assert.NotEqual(earlierExplicitScan, correlatedScan);
            Assert.Equal(replayedJob, explicitRescan);
        }

        [Fact]
        public async Task ScanQueue_ConcurrentCorrelationDispatchesOnlyOneJob()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive));
            var audiobook = new AudiobookBuilder()
                .WithId(1003)
                .WithTitle("Concurrent Move Scan")
                .Build();
            const string correlationId = "move:concurrent-scan";

            var jobs = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                queue.EnqueueScanAsync(audiobook, correlationId: correlationId)));

            Assert.Single(jobs.Distinct());
            Assert.True(queue.Reader.TryRead(out var queued));
            Assert.Equal(jobs[0], queued.Id);
            Assert.False(queue.Reader.TryRead(out _));
        }

        [Fact]
        public async Task ScanQueue_ManualMoveRetryDefersAndReleasesHandoffWhenAnotherScanIsActive()
        {
            var handoffId = Guid.NewGuid();
            var moveJobId = Guid.NewGuid();
            var store = new Mock<IMoveScanHandoffStore>();
            store.Setup(candidate => candidate.RequeueAsync(
                    handoffId,
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.ReleaseClaimAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.MarkDispatchedAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<Guid>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(candidate => candidate.TryClaimAsync(
                    handoffId,
                    It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MoveScanHandoffClaim(
                    handoffId,
                    moveJobId,
                    1004,
                    "/library/book",
                    CreateUnixIdentity(),
                    2,
                    "manual-worker",
                    2));
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive),
                store.Object,
                TimeProvider.System);
            var audiobook = new AudiobookBuilder()
                .WithId(1004)
                .WithTitle("Deferred Move Retry")
                .Build();
            var original = await queue.EnqueueMoveHandoffScanAsync(
                audiobook,
                new MoveScanHandoffClaim(
                    handoffId,
                    moveJobId,
                    audiobook.Id,
                    "/library/book",
                    CreateUnixIdentity(),
                    1,
                    "initial-worker",
                    1));
            Assert.NotNull(original);
            Assert.True(queue.Reader.TryRead(out var originalJob));
            Assert.Equal("/library/book", originalJob.Path);
            queue.UpdateJobStatus(original!.Value, "Failed", "first attempt failed");
            var ordinary = await queue.EnqueueScanAsync(audiobook);
            Assert.NotEqual(original.Value, ordinary);

            var retried = await queue.RequeueScanAsync(original.Value);

            Assert.Null(retried);
            store.Verify(candidate => candidate.RequeueAsync(
                handoffId,
                original.Value,
                1,
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
            store.Verify(candidate => candidate.ReleaseClaimAsync(
                handoffId,
                "manual-worker",
                2,
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
            store.Verify(candidate => candidate.MarkDispatchedAsync(
                handoffId,
                It.IsAny<string>(),
                1,
                original.Value,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScanQueue_TerminalPersistenceDoesNotHoldQueueGate()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive));
            var audiobook = new AudiobookBuilder()
                .WithId(1005)
                .WithTitle("Terminal Persistence")
                .Build();
            var original = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(original, "Processing");
            var persistenceEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePersistence = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = queue.CommitTerminalJobStatusAsync(
                original,
                async () =>
                {
                    persistenceEntered.TrySetResult();
                    await releasePersistence.Task;
                    return ("Completed", (string?)null);
                });
            await persistenceEntered.Task;

            var otherAudiobook = new AudiobookBuilder()
                .WithId(1007)
                .WithTitle("Independent Scan")
                .Build();
            var otherEnqueue = queue.EnqueueScanAsync(otherAudiobook);
            var otherJobId = await otherEnqueue.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEqual(original, otherJobId);

            releasePersistence.TrySetResult();
            await completion;
            Assert.True(queue.TryGetJob(original, out var completed));
            Assert.Equal("Completed", completed!.Status);
        }

        [Fact]
        public async Task ScanQueue_TerminalCommitUsesAuthoritativePersistedOutcome()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive));
            var audiobook = new AudiobookBuilder()
                .WithId(1006)
                .WithTitle("Authoritative Terminal State")
                .Build();
            var jobId = await queue.EnqueueScanAsync(audiobook);
            Assert.True(queue.Reader.TryRead(out _));
            queue.UpdateJobStatus(jobId, "Processing");

            await queue.CommitTerminalJobStatusAsync(
                jobId,
                () => Task.FromResult(("Failed", (string?)"durable failure")));

            Assert.True(queue.TryGetJob(jobId, out var terminal));
            Assert.Equal("Failed", terminal!.Status);
            Assert.Equal("durable failure", terminal.Error);
        }

        [Fact]
        public async Task ScanQueue_FailedCorrelationCreatesReplacementHandoff()
        {
            var queue = new ScanQueueService(
                NullLogger<ScanQueueService>.Instance,
                BuildResolver(FileSystemCaseSensitivity.Sensitive));
            var audiobook = new AudiobookBuilder()
                .WithId(1005)
                .WithTitle("Failed Move Scan")
                .Build();
            const string correlationId = "move:failed-scan";

            var failedJob = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);
            queue.UpdateJobStatus(failedJob, "Failed", "Transient failure");
            var replacement = await queue.EnqueueScanAsync(
                audiobook,
                correlationId: correlationId);

            Assert.NotEqual(failedJob, replacement);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        public async Task UnmatchedScanQueue_DedupeUsesResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            bool shouldDedupe)
        {
            var queue = new UnmatchedScanQueueService(
                NullLogger<UnmatchedScanQueueService>.Instance,
                BuildResolver(caseSensitivity));
            var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "listenarr-unmatched-queue"));
            var first = Path.Join(root, "CaseRoot");
            var second = Path.Join(root, "caseroot");

            var firstJob = await queue.EnqueueAsync(first);
            var secondJob = await queue.EnqueueAsync(second);

            Assert.Equal(shouldDedupe, firstJob == secondJob);
        }

        private static PathIdentitySnapshot CreateUnixIdentity() =>
            PathIdentitySnapshot.FromResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    FileSystemCaseSensitivity.Sensitive),
                FileSystemCaseSensitivityMode.Sensitive,
                "/library",
                "/library/book");

        private static IFileSystemSemanticsResolver BuildResolver(FileSystemCaseSensitivity caseSensitivity)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(r => r.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, caseSensitivity),
                        PathIdentityState.Valid,
                        path)));
            return resolver.Object;
        }
    }
}
