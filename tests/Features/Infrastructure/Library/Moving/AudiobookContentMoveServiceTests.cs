using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "AudiobookContentMoveServiceTests")]
    [Trait("Category", "BackgroundWorkers")]
    public partial class AudiobookContentMoveServiceTests : BaseTests
    {
        private const string TestLeaseOwner = "test-worker";

        private static MoveLeaseToken LeaseToken(int generation = 1) =>
            new(TestLeaseOwner, generation);

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        public void ValidateTargetManifest_CaseOnlyEntriesFollowTargetSemantics(
            FileSystemCaseSensitivity targetCaseSensitivity,
            bool shouldReject)
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-target-manifest-{Guid.NewGuid():N}");
            var targetSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                targetCaseSensitivity);
            var manifest = new[]
            {
                new MoveJobEntry { RelativePath = "Book.m4b", EntryType = MoveJobEntryType.File },
                new MoveJobEntry { RelativePath = "book.m4b", EntryType = MoveJobEntryType.File }
            };

            if (shouldReject)
            {
                var exception = Assert.Throws<MoveNeedsAttentionException>(() =>
                    AudiobookContentMoveService.ValidateTargetManifest(target, manifest, targetSemantics));
                Assert.Contains("cannot represent both", exception.Message);
                return;
            }

            AudiobookContentMoveService.ValidateTargetManifest(target, manifest, targetSemantics);
        }

        [Theory]
        [InlineData("../escape.m4b")]
        [InlineData("/escape.m4b")]
        public void ValidateTargetManifest_RejectsEscapedEntries(string relativePath)
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-target-escape-{Guid.NewGuid():N}");
            var manifest = new[]
            {
                new MoveJobEntry { RelativePath = relativePath, EntryType = MoveJobEntryType.File }
            };

            Assert.Throws<MoveNeedsAttentionException>(() =>
                AudiobookContentMoveService.ValidateTargetManifest(
                    target,
                    manifest,
                    FileSystemPathSemantics.CurrentHostDefault));
        }

        [Fact]
        public async Task MoveContentsAsync_NormalMove_MovesContentsAndDeletesSource()
        {
            var source = FileService.GetTempDirectory("content-move-normal-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var extras = Path.Join(source, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-normal-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(source), result.Source);
            Assert.Equal(Path.GetFullPath(target), result.Target);
            Assert.False(result.TargetInsideSource);
            Assert.False(result.SourceInsideTarget);
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "extras", "cover.jpg")));
        }

        [Fact]
        public async Task MoveContentsAsync_StaleLeaseGeneration_DoesNotMutateFilesystem()
        {
            var source = FileService.GetTempDirectory("content-move-stale-lease-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-stale-lease-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseGeneration = 2,
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() => service.MoveContentsAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Theory]
        [InlineData("owner")]
        [InlineData("generation")]
        [InlineData("expiration")]
        [InlineData("status")]
        public async Task MoveContentsAsync_InvalidLeaseState_DoesNotMutateFilesystem(string invalidState)
        {
            var source = FileService.GetTempDirectory($"content-move-invalid-lease-src-{invalidState}");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-invalid-lease-dst-{invalidState}-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var job = await db.MoveJobs.SingleAsync(job => job.Id == jobId);
                switch (invalidState)
                {
                    case "owner":
                        job.LeaseOwner = "other-worker";
                        break;
                    case "generation":
                        job.LeaseGeneration = 2;
                        break;
                    case "expiration":
                        job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                        break;
                    case "status":
                        job.Status = MoveJobStatus.Completed;
                        break;
                }

                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_DefaultLeaseGeneration_DoesNotMutateFilesystem()
        {
            var source = FileService.GetTempDirectory("content-move-zero-lease-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-zero-lease-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseGeneration = 1,
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() => service.MoveContentsAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(0)),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_UnmarkedJobShapedTempDirectory_IsPreservedAndRequiresAttention()
        {
            var source = FileService.GetTempDirectory("content-move-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "complete audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-partial-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var targetParent = Path.GetDirectoryName(target)!;
            var tempName = Path.Join(targetParent, Path.GetFileName(target) + ".tmp-" + jobId.ToString("N"));
            Directory.CreateDirectory(tempName);
            var unrelatedFile = Path.Join(tempName, "book.m4b");
            await File.WriteAllTextAsync(unrelatedFile, "unrelated bytes");

            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("ownership marker", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("complete audio", await File.ReadAllTextAsync(sourceFile));
            Assert.True(Directory.Exists(tempName));
            Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(unrelatedFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_DirectCopyMarkerWithoutManifest_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-direct-retry-src");
            await FileService.GetFileAsync(source, "book.m4b", "complete audio");
            var target = FileService.GetTempDirectory("content-move-direct-retry-dst");
            await FileService.GetFileAsync(target, "book.m4b", "partial");
            var jobId = Guid.NewGuid();
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("without a persisted manifest", exception.Message);
            Assert.True(Directory.Exists(source));
            Assert.Equal("partial", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetInsideSource_MovesContentsIntoChildAndKeepsTarget()
        {
            var source = FileService.GetTempDirectory("content-move-child-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var extras = Path.Join(source, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var target = Path.Join(source, " test");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.True(result.TargetInsideSource);
            Assert.False(result.SourceInsideTarget);
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(target));
            Assert.False(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(extras));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "extras", "cover.jpg")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetDeepInsideSource_WithSiblingContent_FailsWithoutDeletingAnything()
        {
            var source = FileService.GetTempDirectory("content-move-deep-child-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var targetAncestor = Path.Join(source, "container");
            Directory.CreateDirectory(targetAncestor);
            await FileService.GetFileAsync(targetAncestor, "stale-sibling.txt", "stale");
            var target = Path.Join(targetAncestor, "target");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unexpected content", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(target));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(targetAncestor, "stale-sibling.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideTarget_MovesContentsUpAndDeletesOldChild()
        {
            var target = FileService.GetTempDirectory("content-move-parent-target");
            var source = Path.Join(target, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(result.TargetInsideSource);
            Assert.True(result.SourceInsideTarget);
            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideTarget_WithUnrelatedSibling_Fails()
        {
            var target = FileService.GetTempDirectory("content-move-parent-with-sibling-target");
            var source = Path.Join(target, "Title");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var sibling = Path.Join(target, "OtherBook");
            Directory.CreateDirectory(sibling);
            await FileService.GetFileAsync(sibling, "other.m4b", "other");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unrelated content", ex.Message);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(sibling, "other.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideEmptyParent_DoesNotDeleteParent()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-empty-parent");
            var source = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-cleaned-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_WithCleanupBoundary_RemovesEmptyAncestorsButKeepsBoundary()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-cleanup-boundary");
            var source = Path.Join(sourceRoot, "Author", "Series", "Title", "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-cleanup-boundary-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.True(Directory.Exists(Path.Join(sourceRoot, "Author")));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_SourceEqualsCleanupBoundary_PreservesBoundaryDirectory()
        {
            var source = FileService.GetTempDirectory("content-move-source-is-boundary");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-source-is-boundary-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: source);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_ExistingEmptyTarget_PrunesSourceParentAfterNestedQuarantineCleanup()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-existing-target-root");
            var series = Path.Join(sourceRoot, "Matt Dinniman", "Dungeon Crawler Carl");
            var oldTitle = Path.Join(series, "A Parade of Horribles (2026)");
            var source = Path.Join(oldTitle, "test");
            var sourceDisc = Path.Join(source, "Disc 01");
            Directory.CreateDirectory(sourceDisc);
            await FileService.GetFileAsync(sourceDisc, "book.m4b", "audio");
            var target = Path.Join(series, "A Parade of Horribles (20262)", "test");
            Directory.CreateDirectory(target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(oldTitle));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldTitle));
            Assert.True(File.Exists(result.RecoveryMarkerPath));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
            Assert.True(File.Exists(Path.Join(target, "Disc 01", "book.m4b")));
            Assert.True(Directory.Exists(sourceRoot));
        }

        [Fact]
        public async Task FinalizeMove_MissingCleanupBoundary_LeavesMarkerAndRequiresAttention()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-missing-boundary-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-missing-boundary-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("no source cleanup boundary", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            Assert.True(Directory.Exists(oldTitle));
            Assert.False(Directory.Exists(source));
        }

        [Fact]
        public async Task FinalizeMove_RetryAfterImmediateParentRemoved_PrunesHigherEmptyAncestors()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-retry-root");
            var author = Path.Join(sourceRoot, "Author");
            var oldTitle = Path.Join(author, "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-finalize-retry-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Directory.Delete(oldTitle, false);
            Assert.True(Directory.Exists(author));
            Assert.False(Directory.Exists(oldTitle));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(author));
            Assert.True(Directory.Exists(sourceRoot));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
        }

        [Fact]
        public async Task FinalizeMove_MissingBoundaryWithNonEmptyParent_CompletesAtNaturalStop()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-nonempty-stop-parent");
            await FileService.GetFileAsync(sourceParent, "keep.txt", "keep");
            var source = Path.Join(sourceParent, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nonempty-stop-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(sourceParent, "keep.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_DeleteEmptySourceFalse_KeepsEmptySourceDirectory()
        {
            var source = FileService.GetTempDirectory("content-move-keep-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-keep-source-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target, deleteEmptySource: false),
                CancellationToken.None);

            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideNonEmptyParent_DoesNotDeleteParent()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-nonempty-parent");
            await FileService.GetFileAsync(sourceParent, "keep.txt", "keep");
            var source = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nonempty-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(sourceParent, "keep.txt")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetContainsUnrelatedFiles_Fails()
        {
            var source = FileService.GetTempDirectory("content-move-collision-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-collision-dst");
            await FileService.GetFileAsync(target, "existing.txt", "blocked");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("contains files", ex.Message);
            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "existing.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetContainsOnlySourceSubtree_AllowsMove()
        {
            var target = FileService.GetTempDirectory("content-move-source-subtree-target");
            var source = Path.Join(target, "nested", "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Join(target, "nested")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetInsideSource_TargetAlreadyContainsFile_Fails()
        {
            var source = FileService.GetTempDirectory("content-move-child-collision-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(source, " test");
            Directory.CreateDirectory(target);
            await FileService.GetFileAsync(target, "existing.txt", "blocked");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("contains files", ex.Message);
            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "existing.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceChangesAfterPublish_BlocksAllCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-drift-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-drift-dst-{Guid.NewGuid():N}");
            var faultInjector = new AddSourceFileAfterPublish(source);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                faultInjector);

            var request = await CreateLeasedMoveRequestAsync(source, target);
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() => service.MoveContentsAsync(
                request,
                CancellationToken.None));

            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetChangesAfterPublish_BlocksSourceCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-target-drift-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-target-drift-dst-{Guid.NewGuid():N}");
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new AddTargetFileAfterPublish(target));

            var request = await CreateLeasedMoveRequestAsync(source, target);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "arrived-late.txt")));
        }

        [Fact]
        public async Task LegacyCopyCompleteMarker_WithoutManifest_NeverAuthorizesDeletion()
        {
            var source = FileService.GetTempDirectory("content-move-legacy-marker-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-legacy-marker-dst");
            await FileService.GetFileAsync(target, "book.m4b", "audio");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await File.WriteAllTextAsync(
                Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                "copy-complete");
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.GetRecoverableMoveAsync(request));

            Assert.Contains("obsolete pre-release", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, $".listenarr-move-{jobId:N}.pending")));
        }

        [Fact]
        public async Task AtomicRenameMarker_RecoversBeforePhasePersistence()
        {
            var source = FileService.GetTempDirectory("content-move-atomic-recovery-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-atomic-recovery-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await File.WriteAllTextAsync(
                Path.Join(source, $".listenarr-move-{jobId:N}.pending"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = 1,
                    JobId = jobId,
                    Source = Path.GetFullPath(source),
                    Target = Path.GetFullPath(target),
                    Stage = "atomic-rename-complete"
                }));
            Directory.Move(source, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.GetRecoverableMoveAsync(request);

            Assert.NotNull(result);
            Assert.True(result.SourceCleanupCompleted);
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task GetRecoverableMoveAsync_PropagatesCancellationDuringManifestVerification()
        {
            var source = FileService.GetTempDirectory("content-move-cancel-recovery-src");
            var target = FileService.GetTempDirectory("content-move-cancel-recovery-dst");
            var destination = await FileService.GetFileAsync(target, "book.m4b", "audio");
            var jobId = Guid.NewGuid();
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-complete");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified
                });
                await db.SaveChangesAsync();
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.GetRecoverableMoveAsync(
                    new AudiobookContentMoveRequest(
                        source,
                        target,
                        jobId,
                        true,
                        FileSystemPathSemantics.CurrentHostDefault,
                        FileSystemPathSemantics.CurrentHostDefault,
                        LeaseToken(1)),
                    cancellation.Token));
        }

        [Fact]
        public async Task ResumeSourceCleanup_VerifiedQuarantine_ConvergesAfterCrash()
        {
            var source = FileService.GetTempDirectory("content-move-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-quarantine-dst");
            var jobId = Guid.NewGuid();
            var quarantineRoot = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{jobId:N}");
            Directory.CreateDirectory(quarantineRoot);
            await WriteQuarantineOwnershipMarkerAsync(
                quarantineRoot,
                jobId,
                source,
                target);
            var destination = Path.Join(target, "book.m4b");
            var quarantineFile = Path.Join(quarantineRoot, "book.m4b");
            await File.WriteAllTextAsync(destination, "verified audio");
            await File.WriteAllTextAsync(quarantineFile, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var resumed = await service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false),
                CancellationToken.None);

            Assert.True(resumed.SourceCleanupCompleted);
            Assert.False(File.Exists(quarantineFile));
            Assert.False(Directory.Exists(quarantineRoot));
            Assert.False(Directory.Exists(source));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                (await verification.MoveJobEntries.SingleAsync()).CleanupState);
        }

        [Fact]
        public async Task ResumeSourceCleanup_DeletedQuarantine_ConvergesAfterCrash()
        {
            var source = FileService.GetTempDirectory("content-move-deleted-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-deleted-quarantine-dst");
            var jobId = Guid.NewGuid();
            var destination = Path.Join(target, "book.m4b");
            await File.WriteAllTextAsync(destination, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var resumed = await service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false),
                CancellationToken.None);

            Assert.True(resumed.SourceCleanupCompleted);
            Assert.False(Directory.Exists(source));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                (await verification.MoveJobEntries.SingleAsync()).CleanupState);
        }

        [Fact]
        public async Task ResumeSourceCleanup_SourceAndQuarantineBothExist_BlocksCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-ambiguous-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-ambiguous-quarantine-dst");
            var jobId = Guid.NewGuid();
            var quarantineRoot = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{jobId:N}");
            Directory.CreateDirectory(quarantineRoot);
            await WriteQuarantineOwnershipMarkerAsync(
                quarantineRoot,
                jobId,
                source,
                target);
            var sourceFile = Path.Join(source, "book.m4b");
            var destination = Path.Join(target, "book.m4b");
            var quarantineFile = Path.Join(quarantineRoot, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "verified audio");
            await File.WriteAllTextAsync(destination, "verified audio");
            await File.WriteAllTextAsync(quarantineFile, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() => service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(quarantineFile));
        }

        [Fact]
        public async Task MoveContentsAsync_CopyStartedMarkerOwnedByAnotherJob_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-wrong-marker-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-wrong-marker-dst");
            var jobId = Guid.NewGuid();
            await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await File.WriteAllTextAsync(
                Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = 1,
                    JobId = Guid.NewGuid(),
                    Source = source,
                    Target = target,
                    Stage = "copy-started"
                }));

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = new AudiobookContentMoveRequest(
                source,
                target,
                jobId,
                true,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault,
                LeaseToken(1));

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
        }

        [Fact]
        public async Task MoveContentsAsync_CopyStartedWithUnknownDestinationFile_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-unowned-target-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-unowned-target-dst");
            await FileService.GetFileAsync(target, "unrelated.txt", "not owned");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(Path.Join(target, "unrelated.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_ValidOwnedPartial_PublishesFromPersistedManifest()
        {
            var source = FileService.GetTempDirectory("content-move-valid-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-valid-partial-dst");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            var partial = Path.Join(target, $"book.m4b.listenarr-{jobId:N}.partial");
            await File.WriteAllTextAsync(partial, "verified audio");
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(File.Exists(partial));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            Assert.False(Directory.Exists(source));
        }

        [Fact]
        public async Task MoveContentsAsync_InvalidOwnedPartial_IsPreservedAndRequiresAttention()
        {
            var source = FileService.GetTempDirectory("content-move-invalid-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-invalid-partial-dst");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            var partial = Path.Join(target, $"book.m4b.listenarr-{jobId:N}.partial");
            await File.WriteAllTextAsync(partial, "invalid bytes");
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("partial file does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("invalid bytes", await File.ReadAllTextAsync(partial));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(Directory.Exists(source));
        }

        private async Task PersistFileManifestAsync(
            Guid jobId,
            string relativePath,
            string sourceFile)
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(sourceFile)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.MoveJobEntries.Add(new MoveJobEntry
            {
                MoveJobId = jobId,
                RelativePath = relativePath,
                EntryType = MoveJobEntryType.File,
                Length = new FileInfo(sourceFile).Length,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(sourceFile),
                Sha256 = hash
            });
            await db.SaveChangesAsync();
        }

        private static Task WriteQuarantineOwnershipMarkerAsync(
            string quarantineRoot,
            Guid jobId,
            string source,
            string target)
        {
            var marker = System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                ArtifactType = "quarantine-directory",
                JobId = jobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                DirectoryPath = Path.GetFullPath(quarantineRoot),
                OwnedArtifactType = (string?)null
            });
            return File.WriteAllTextAsync(
                Path.Join(quarantineRoot, ".listenarr-quarantine-owner.json"),
                marker);
        }

        private async Task<AudiobookContentMoveRequest> CreateLeasedMoveRequestAsync(
            string source,
            string target,
            Guid? jobId = null,
            bool deleteEmptySource = true,
            FileSystemPathSemantics? sourceSemantics = null,
            FileSystemPathSemantics? targetSemantics = null,
            string? sourceCleanupBoundary = null)
        {
            var id = jobId ?? Guid.NewGuid();
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.MoveJobs.Add(new MoveJob
            {
                Id = id,
                AudiobookId = 1,
                RequestedPath = target,
                SourcePath = source,
                Status = MoveJobStatus.Running,
                LeaseOwner = TestLeaseOwner,
                LeaseGeneration = 1,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{id:N}"
            });
            await db.SaveChangesAsync();

            return new AudiobookContentMoveRequest(
                source,
                target,
                id,
                deleteEmptySource,
                sourceSemantics ?? FileSystemPathSemantics.CurrentHostDefault,
                targetSemantics ?? sourceSemantics ?? FileSystemPathSemantics.CurrentHostDefault,
                LeaseToken(1),
                sourceCleanupBoundary);
        }

        private sealed class AddSourceFileAfterPublish(string source) : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
                File.WriteAllTextAsync(
                    Path.Join(source, "arrived-late.txt"),
                    "new content",
                    cancellationToken);
        }

        private sealed class AddTargetFileAfterPublish(string target) : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
                File.WriteAllTextAsync(
                    Path.Join(target, "arrived-late.txt"),
                    "new target content",
                    cancellationToken);
        }
    }
}
