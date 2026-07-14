using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "MoveJobProcessorTests")]
    [Trait("Category", "BackgroundWorkers")]
    public partial class MoveJobProcessorTests : BaseTests
    {
        private const string LeaseOwner = "test-worker";
        [Fact]
        public async Task ProcessJobAsync_HappyPath_MovesFilesAndCompletesJob()
        {
            var src = FileService.GetTempDirectory("move-processor-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), "move-processor-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));

            var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
            var moveEvent = Assert.Single(history, entry => entry.EventType == "Moved");
            Assert.True(moveEvent.NotificationSent);

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.started", It.IsAny<double>()), Times.Once);
            metricsMock.Verify(m => m.Increment("worker.move.job.completed", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_RemovesEmptySourceAncestorsWithinConfiguredRoot()
        {
            var sourceRoot = FileService.GetTempDirectory("move-processor-cleanup-root");
            var source = Path.Join(sourceRoot, "Author", "Series", "Title", "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-cleanup-dst-{Guid.NewGuid():N}");
            var rootFolderRepository = _provider.GetRequiredService<IRootFolderRepository>();
            await rootFolderRepository.AddAsync(new RootFolder { Name = "Cleanup Root", Path = sourceRoot });
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Cleanup Test", BasePath = source });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CustomSiblingMove_RemovesOldTitleFolderAndKeepsSeries()
        {
            var customRoot = FileService.GetTempDirectory("move-processor-sibling-root");
            var series = Path.Join(customRoot, "Matt Dinniman", "Dungeon Crawler Carl");
            var oldTitle = Path.Join(series, "A Parade of Horribles (20262)");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
            Directory.CreateDirectory(target);
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "A Parade of Horribles",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(oldTitle));
            Assert.True(Directory.Exists(series));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_UnrelatedForeignSyntaxRoot_DoesNotBlockBoundedCleanup()
        {
            var sourceRoot = FileService.GetTempDirectory("move-processor-foreign-root");
            var source = Path.Join(sourceRoot, "Author", "Title");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-foreign-dst-{Guid.NewGuid():N}");
            var rootFolderRepository = _provider.GetRequiredService<IRootFolderRepository>();
            await rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Foreign Legacy Root",
                Path = OperatingSystem.IsWindows() ? "/legacy/library" : @"Z:\legacy\library"
            });
            await rootFolderRepository.AddAsync(new RootFolder { Name = "Valid Root", Path = sourceRoot });
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foreign Root Cleanup",
                BasePath = source
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CompletedStatusPersistenceFailure_PropagatesWithoutCompletedMetric()
        {
            var source = FileService.GetTempDirectory("move-processor-status-failure-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), "move-processor-status-failure-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Status Failure",
                BasePath = source
            });
            var (durableQueue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
            var handoffStore = new Mock<IMoveScanHandoffStore>();
            handoffStore.Setup(store => store.CommitMoveCompletionAsync(
                    It.IsAny<MoveCompletionCommit>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PersistenceException(
                    "Completion transaction failed.",
                    new InvalidOperationException("Database unavailable.")));
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                handoffStore.Object);

            await Assert.ThrowsAsync<PersistenceException>(() => processor.ProcessJobAsync(
                job,
                CancellationToken.None));

            var metrics = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            Assert.Equal(
                MoveJobStatus.Running,
                (await durableQueue.GetJobAsync(job.Id))?.Status);
            metrics.Verify(
                service => service.Increment("worker.move.job.completed", It.IsAny<double>()),
                Times.Never);
            Assert.Empty(Directory.EnumerateFiles(
                target,
                $".listenarr-move-{job.Id:N}.pending",
                SearchOption.TopDirectoryOnly));
            var persistedJob = await durableQueue.GetJobAsync(job.Id);
            Assert.Equal(MoveJobPhase.RecordingCompletion, persistedJob?.Phase);
            Assert.Empty(await _historyRepository.GetByEventTypeAsync("MoveFailed"));
            Assert.Empty(
                await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));

            var retryProcessor = _provider.GetRequiredService<IMoveJobProcessor>();
            await retryProcessor.ProcessJobAsync(persistedJob!, CancellationToken.None);

            var completedJob = await durableQueue.GetJobAsync(job.Id);
            Assert.True(
                completedJob?.Status == MoveJobStatus.Completed,
                $"Expected completed replay, but got {completedJob?.Status}: {completedJob?.Error}");
            Assert.Equal(MoveJobPhase.RecordingCompletion, completedJob?.Phase);
            Assert.Single(
                await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"),
                entry => entry.EventType == "Moved");
            metrics.Verify(
                service => service.Increment("worker.move.job.completed", It.IsAny<double>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_TargetInsideSource_MovesSourceContentsIntoTarget()
        {
            var src = FileService.GetTempDirectory("move-processor-nested-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var extras = Path.Join(src, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var dst = Path.Join(src, " test");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Nested", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.True(Directory.Exists(src));
            Assert.True(Directory.Exists(dst));
            Assert.False(File.Exists(Path.Join(src, "book.m4b")));
            Assert.False(Directory.Exists(extras));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
            Assert.True(File.Exists(Path.Join(dst, "extras", "cover.jpg")));

            using var verificationScope = _provider.CreateScope();
            var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var updatedAudiobook = await verificationRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updatedAudiobook);
            Assert.Equal(dst, updatedAudiobook!.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_CustomMove_RemovesEmptySourceParentUsingFallbackBoundary()
        {
            var sourceParent = FileService.GetTempDirectory("move-processor-empty-parent");
            var src = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), "move-processor-cleaned-dst");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Empty Parent", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.False(Directory.Exists(src));
            Assert.False(Directory.Exists(sourceParent));
            Assert.True(Directory.Exists(Path.GetDirectoryName(sourceParent)!));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_SourceInsideDestination_DoesNotDeleteDestinationAncestor()
        {
            var dst = FileService.GetTempDirectory("move-processor-parent-target");
            var src = Path.Join(dst, " test");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Parent Target", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(Directory.Exists(dst));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_CaseOnlyMove_OnCaseSensitiveHost_MovesFiles()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var root = FileService.GetTempDirectory("move-processor-case-only-root");
            var src = Path.Join(root, "Title");
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(root, "title");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Case", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.False(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_DeleteEmptySourceFalse_KeepsEmptySourceDirectory()
        {
            var src = FileService.GetTempDirectory("move-processor-keep-source");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-keep-source-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Keep Source",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                dst,
                src,
                deleteEmptySource: false);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Completed, updatedJob!.Status);
            Assert.True(Directory.Exists(src));
            Assert.Empty(Directory.EnumerateFileSystemEntries(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_RequeuedCompletedMoveWithRetainedSource_RemainsCompleted()
        {
            var src = FileService.GetTempDirectory("move-processor-requeue-retained-source");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-requeue-retained-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Requeue Retained",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(
                audiobook,
                dst,
                src,
                deleteEmptySource: false);
            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var requeuedJobId = await queue.RequeueMoveAsync(job.Id);
            Assert.NotNull(requeuedJobId);
            var requeuedJob = await queue.GetJobAsync(requeuedJobId!.Value);
            Assert.NotNull(requeuedJob);
            await PrepareJobForProcessingAsync(queue, requeuedJob!);
            await processor.ProcessJobAsync(requeuedJob!, CancellationToken.None);

            var completedRequeue = await queue.GetJobAsync(requeuedJob.Id);
            Assert.NotNull(completedRequeue);
            Assert.Equal(MoveJobStatus.Completed, completedRequeue!.Status);
            Assert.True(Directory.Exists(src));
            Assert.Empty(Directory.EnumerateFileSystemEntries(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_TargetContainsFiles_RequiresAttention()
        {
            var src = FileService.GetTempDirectory("move-processor-fail-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-processor-fail-dst");
            await FileService.GetFileAsync(dst, "existing.txt", "blocked");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Fail", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
            Assert.Equal(0, updatedJob.AttemptCount);
            Assert.True(Directory.Exists(src));

            var metricsMock = _provider.GetRequiredService<Mock<IAppMetricsService>>();
            metricsMock.Verify(m => m.Increment("worker.move.job.needs_attention", It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task ProcessJobAsync_AttemptIncrementLosesLease_DoesNotPublishStaleFailure()
        {
            var src = FileService.GetTempDirectory("move-processor-stale-attempt-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-stale-attempt-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Stale Attempt",
                BasePath = src
            });
            var (_, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);
            var queue = new Mock<IMoveQueueService>();
            queue.Setup(service => service.UpdateJobStatusAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    MoveJobStatus.Running,
                    null,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            queue.Setup(service => service.IncrementAttemptAsync(
                    job.Id,
                    LeaseOwner,
                    job.LeaseGeneration,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MoveLeaseLostException(job.Id, job.LeaseGeneration));
            var contentMoveService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ThrowUnexpectedAfterPublish());
            var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
                _provider,
                queue.Object,
                contentMoveService);

            await Assert.ThrowsAsync<MoveLeaseLostException>(() => processor.ProcessJobAsync(
                job,
                CancellationToken.None));

            queue.Verify(service => service.UpdateJobStatusAsync(
                job.Id,
                LeaseOwner,
                job.LeaseGeneration,
                MoveJobStatus.Failed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcessJobAsync_CanceledToken_ThrowsBeforeStateChange()
        {
            var src = FileService.GetTempDirectory("move-processor-cancel");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-cancel-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook { Title = "Move Processor Cancel", BasePath = src });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, src);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(job, cts.Token));

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Running, updatedJob!.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyIdenticalEndpoint_IsSupersededWithoutHistoryOrScanHandoff()
        {
            var src = FileService.GetTempDirectory("move-processor-identical-legacy");
            var sourceFile = await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Identical Legacy",
                BasePath = src
            });
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
            var identity = new PathIdentitySnapshot(
                syntax,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                src);
            var legacyJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = src,
                RequestedPath = src,
                Status = MoveJobStatus.Queued,
                ActiveDeduplicationKey = $"legacy-identical:{Guid.NewGuid():N}"
            };
            legacyJob.SetSourceIdentity(identity);
            legacyJob.SetTargetIdentity(identity);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(legacyJob);
                await db.SaveChangesAsync();
            }

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var job = await queue.GetJobAsync(legacyJob.Id);
            Assert.NotNull(job);
            await PrepareJobForProcessingAsync(queue, job!);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job!, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(legacyJob.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.Superseded, updatedJob!.Status);
            Assert.Contains("identical", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(updatedJob.ActiveDeduplicationKey);
            Assert.Null(updatedJob.LeaseOwner);
            Assert.Null(updatedJob.LeaseExpiresAt);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{legacyJob.Id:N}"));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.False(await verification.MoveScanHandoffs
                .AnyAsync(handoff => handoff.MoveJobId == legacyJob.Id));
        }

        [Theory]
        [InlineData("temporary-directory", false)]
        [InlineData("temporary-directory", true)]
        [InlineData("quarantine-directory", false)]
        [InlineData("quarantine-directory", true)]
        [InlineData("target-scaffold-temporary", false)]
        [InlineData("target-scaffold-temporary", true)]
        [InlineData("target-scaffold-quarantine", false)]
        [InlineData("target-scaffold-quarantine", true)]
        public async Task ProcessJobAsync_LegacyIdenticalEndpointWithCleanupTombstone_PreservesForAttention(
            string artifactType,
            bool interruptedWrite)
        {
            var src = FileService.GetTempDirectory("move-processor-identical-tombstone");
            var sourceFile = await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Identical Tombstone",
                BasePath = src
            });
            var identity = new PathIdentitySnapshot(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                src);
            var legacyJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = src,
                RequestedPath = src,
                Status = MoveJobStatus.Queued,
                ActiveDeduplicationKey = $"legacy-identical-tombstone:{Guid.NewGuid():N}"
            };
            legacyJob.SetSourceIdentity(identity);
            legacyJob.SetTargetIdentity(identity);
            var parent = Path.GetDirectoryName(src)!;
            var tombstonePath = Path.Join(
                parent,
                $".listenarr-{artifactType}-{legacyJob.Id:N}.cleanup.json");
            var evidencePath = interruptedWrite
                ? tombstonePath + $".writing-{Guid.NewGuid():N}"
                : tombstonePath;
            await File.WriteAllTextAsync(evidencePath, "{}");

            try
            {
                var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
                await using (var db = await factory.CreateDbContextAsync())
                {
                    db.MoveJobs.Add(legacyJob);
                    await db.SaveChangesAsync();
                }

                var queue = _provider.GetRequiredService<IMoveQueueService>();
                var job = await queue.GetJobAsync(legacyJob.Id);
                Assert.NotNull(job);
                await PrepareJobForProcessingAsync(queue, job!);

                await _provider.GetRequiredService<IMoveJobProcessor>()
                    .ProcessJobAsync(job!, CancellationToken.None);

                var updatedJob = await queue.GetJobAsync(legacyJob.Id);
                Assert.NotNull(updatedJob);
                Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
                Assert.Contains("sibling artifacts", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(sourceFile));
                Assert.True(File.Exists(evidencePath));
                Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{legacyJob.Id:N}"));
                await using var verification = await factory.CreateDbContextAsync();
                Assert.False(await verification.MoveScanHandoffs
                    .AnyAsync(handoff => handoff.MoveJobId == legacyJob.Id));
            }
            finally
            {
                if (File.Exists(evidencePath))
                {
                    File.Delete(evidencePath);
                }
            }
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyIdenticalEndpointWithExecutionState_PreservesForAttention()
        {
            var src = FileService.GetTempDirectory("move-processor-identical-evidence");
            var sourceFile = await FileService.GetFileAsync(src, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Identical Evidence",
                BasePath = src
            });
            var identity = new PathIdentitySnapshot(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                src);
            var legacyJob = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = src,
                RequestedPath = src,
                Status = MoveJobStatus.Queued,
                ActiveDeduplicationKey = $"legacy-identical-evidence:{Guid.NewGuid():N}"
            };
            legacyJob.SetSourceIdentity(identity);
            legacyJob.SetTargetIdentity(identity);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(legacyJob);
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = legacyJob.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File
                });
                await db.SaveChangesAsync();
            }

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var job = await queue.GetJobAsync(legacyJob.Id);
            Assert.NotNull(job);
            await PrepareJobForProcessingAsync(queue, job!);

            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job!, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(legacyJob.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
            Assert.Contains("durable move execution state", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(updatedJob.ActiveDeduplicationKey);
            Assert.Null(updatedJob.LeaseOwner);
            Assert.Null(updatedJob.LeaseExpiresAt);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{legacyJob.Id:N}"));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.True(await verification.MoveJobEntries
                .AnyAsync(entry => entry.MoveJobId == legacyJob.Id));
            Assert.False(await verification.MoveScanHandoffs
                .AnyAsync(handoff => handoff.MoveJobId == legacyJob.Id));
        }

        [Fact]
        public async Task ProcessJobAsync_AtomicMarkerWithRecreatedSource_MarksNeedsAttention()
        {
            var src = FileService.GetTempDirectory("move-processor-recovery-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-recovery-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Recovery",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);
            var contentMoveService = _provider.GetRequiredService<AudiobookContentMoveService>();
            await contentMoveService.MoveContentsAsync(
                new AudiobookContentMoveRequest(
                    src,
                    dst,
                    job.Id,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    new MoveLeaseToken(LeaseOwner, job.LeaseGeneration)),
                CancellationToken.None);

            Assert.False(Directory.Exists(src));
            Assert.Single(Directory.EnumerateFiles(dst, ".listenarr-move-*.pending"));
            Directory.CreateDirectory(src);
            await FileService.GetFileAsync(src, "new-content.txt", "do not delete");

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);

            using var verificationScope = _provider.CreateScope();
            var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var updatedAudiobook = await verificationRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updatedAudiobook);
            Assert.Equal(src, updatedAudiobook!.BasePath);
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
            Assert.Equal("do not delete", await File.ReadAllTextAsync(Path.Join(src, "new-content.txt")));
            Assert.Single(Directory.EnumerateFiles(dst, ".listenarr-move-*.pending"));
        }

        [Fact]
        public async Task ProcessJobAsync_CopyCompletedMarkerWithoutManifest_BlocksSourceCleanup()
        {
            var src = FileService.GetTempDirectory("move-processor-copy-complete-src");
            await FileService.GetFileAsync(src, "book.m4b", "audio");
            var dst = FileService.GetTempDirectory("move-processor-copy-complete-dst");
            await FileService.GetFileAsync(dst, "book.m4b", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Copy Complete",
                BasePath = src
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);
            await File.WriteAllTextAsync(
                Path.Join(dst, $".listenarr-move-{job.Id:N}.pending"),
                "copy-complete");

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var completedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(completedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, completedJob!.Status);
            Assert.True(Directory.Exists(src));
            Assert.True(File.Exists(Path.Join(dst, "book.m4b")));
        }

        [Fact]
        public async Task ProcessJobAsync_MissingSourceAndTargetWithTargetMetadata_MarksNeedsAttention()
        {
            var src = Path.Join(FileService.GetTempPath(), $"move-processor-missing-src-{Guid.NewGuid():N}");
            var dst = Path.Join(FileService.GetTempPath(), $"move-processor-missing-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Missing Paths",
                BasePath = dst
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, dst, src);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
        }

        [Fact]
        public async Task ProcessJobAsync_MissingPersistedSource_DoesNotMoveCurrentBasePath()
        {
            var missingSource = Path.Join(FileService.GetTempPath(), $"move-processor-stale-src-{Guid.NewGuid():N}");
            var currentBasePath = FileService.GetTempDirectory("move-processor-current-base");
            await FileService.GetFileAsync(currentBasePath, "current.m4b", "current audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-stale-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Stale Source",
                BasePath = currentBasePath
            });
            var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, missingSource);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(job.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
            Assert.True(File.Exists(Path.Join(currentBasePath, "current.m4b")));
            Assert.False(Directory.Exists(target));
            using var verificationScope = _provider.CreateScope();
            var repository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            Assert.Equal(currentBasePath, (await repository.GetByIdAsync(audiobook.Id))!.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_LegacyJobWithoutSourcePath_RequiresAttentionWithoutMovingCurrentBasePath()
        {
            var source = FileService.GetTempDirectory("move-processor-legacy-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"move-processor-legacy-target-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor Legacy Source",
                BasePath = source
            });
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(audiobook.Id, target, source);
            var job = await queue.GetJobAsync(jobId);
            Assert.NotNull(job);
            job!.SourcePath = null;
            job.SourcePathSyntax = null;
            job.SourceCaseSensitivity = null;
            job.SourceCaseSensitivityMode = null;
            job.SourceIdentityBoundary = null;
            await PrepareJobForProcessingAsync(queue, job);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job, CancellationToken.None);

            var updatedJob = await queue.GetJobAsync(jobId);
            Assert.NotNull(updatedJob);
            Assert.Equal(MoveJobStatus.NeedsAttention, updatedJob!.Status);
            Assert.Contains("persisted source path", updatedJob.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(target));
        }

        private sealed class ThrowUnexpectedAfterPublish : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(
                Guid jobId,
                CancellationToken cancellationToken) =>
                Task.FromException(new InvalidOperationException(
                    "Simulated unexpected post-publication failure."));
        }

        private static async Task PrepareJobForProcessingAsync(IMoveQueueService queue, MoveJob job)
        {
            var leaseGeneration = await queue.TryClaimJobAsync(job.Id, LeaseOwner);
            Assert.NotNull(leaseGeneration);
            job.LeaseOwner = LeaseOwner;
            job.LeaseGeneration = leaseGeneration.Value;
        }

        private async Task<(IMoveQueueService Queue, MoveJob Job)> CreateQueuedMoveJobAsync(
            Audiobook audiobook,
            string requestedPath,
            string sourcePath,
            bool deleteEmptySource = true)
        {
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(
                audiobook.Id,
                requestedPath,
                sourcePath,
                deleteEmptySource);
            var job = await queue.GetJobAsync(jobId);
            Assert.NotNull(job);
            await PrepareJobForProcessingAsync(queue, job!);
            return (queue, job!);
        }
    }
}
