using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_LeaseReplacedBeforeTempPublication_PreservesReplacementWorkersArtifacts()
    {
        var source = FileService.GetTempDirectory("content-move-stale-lease-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-stale-lease-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var targetParent = Path.GetDirectoryName(target)!;
        var tempDirectory = Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseBeforeTempPublication(factory, tempDirectory));

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(tempDirectory));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(tempDirectory, "book.m4b")));
        Assert.True(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
        Assert.False(File.Exists(Path.Join(
            tempDirectory,
            $".listenarr-move-{request.JobId:N}.pending")));
        Assert.Single(Directory.EnumerateFiles(
            tempDirectory,
            $".listenarr-move-{request.JobId:N}.pending.writing-*",
            SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_LeaseReplacedDuringLargeCopy_StopsStaleWriter()
    {
        var source = FileService.GetTempDirectory("content-move-copy-lease-src");
        var sourceFile = Path.Join(source, "book.m4b");
        await File.WriteAllBytesAsync(sourceFile, new byte[2 * 1024 * 1024]);
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-copy-lease-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var partialFile = Path.Join(
            tempDirectory,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseAfterFirstCopyChunk(factory));

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(partialFile));
        Assert.Equal(1024 * 1024, new FileInfo(partialFile).Length);
        Assert.Equal(2 * 1024 * 1024, new FileInfo(sourceFile).Length);
        Assert.True(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
    }

    [Fact]
    public async Task MoveContentsAsync_LeaseReplacedBeforeTempOwnershipPublication_PreservesWriteEvidence()
    {
        var source = FileService.GetTempDirectory("content-move-owner-lease-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-owner-lease-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseBeforeTempOwnershipPublication(factory));

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(tempDirectory));
        Assert.False(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
        Assert.Single(Directory.EnumerateFiles(
            tempDirectory,
            ".listenarr-temp-owner.json.writing-*",
            SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_LeaseReplacedAtScaffoldPublication_DoesNotPublishPreparedHierarchy()
    {
        var scaffoldParent = FileService.GetTempDirectory("content-move-scaffold-lease-root");
        var source = FileService.GetTempDirectory("content-move-scaffold-lease-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(scaffoldParent, "Author", "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var temporaryRoot = Path.Join(
            scaffoldParent,
            $".listenarr-scaffold-{request.JobId:N}");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseBeforeScaffoldPublication(factory));

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Join(scaffoldParent, "Author")));
        Assert.True(Directory.Exists(temporaryRoot));
        Assert.True(File.Exists(Path.Join(temporaryRoot, ".listenarr-scaffold-owner.json")));
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task MoveContentsAsync_LeaseReplacedDuringOwnershipCleanup_PreservesTombstoneForNewWorker()
    {
        var source = FileService.GetTempDirectory("content-move-cleanup-lease-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-cleanup-lease-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseBeforeOwnedDirectoryDelete(factory));

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        var sourceParent = Path.GetDirectoryName(source)!;
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var cleanupDirectory = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup-dir");
        var tombstonePath = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup.json");
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.True(Directory.Exists(cleanupDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cleanupDirectory));
        Assert.True(File.Exists(tombstonePath));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));

        var replacementRequest = request with
        {
            LeaseToken = new MoveLeaseToken("replacement-worker", 2)
        };
        var recovered = await service.GetRecoverableMoveAsync(
            replacementRequest,
            CancellationToken.None);
        Assert.NotNull(recovered);
        var completed = await service.ResumeSourceCleanupAsync(
            replacementRequest,
            recovered!,
            CancellationToken.None);

        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.False(Directory.Exists(cleanupDirectory));
        Assert.False(File.Exists(tombstonePath));
    }

    private sealed class ReplaceLeaseBeforeTempPublication(
        IDbContextFactory<ListenArrDbContext> factory,
        string tempDirectory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnRecoveryMarkerWrite(
            Guid jobId,
            RecoveryMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || faultPoint != RecoveryMarkerWriteFaultPoint.BeforePublication
                || !File.Exists(Path.Join(tempDirectory, "book.m4b")))
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class ReplaceLeaseAfterFirstCopyChunk(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnCopyMutation(
            Guid jobId,
            CopyMutationFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != CopyMutationFaultPoint.AfterChunkWritten)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class ReplaceLeaseBeforeTempOwnershipPublication(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || markerKind != OwnershipMarkerKind.TemporaryDirectory
                || faultPoint != OwnershipMarkerWriteFaultPoint.BeforePublication)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class ReplaceLeaseBeforeScaffoldPublication(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public void OnTargetScaffoldPreparation(
            Guid jobId,
            TargetScaffoldPreparationFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != TargetScaffoldPreparationFaultPoint.BeforePublication)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class ReplaceLeaseBeforeOwnedDirectoryDelete(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnOwnershipCleanup(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipCleanupFaultPoint faultPoint)
        {
            if (_replaced
                || markerKind != OwnershipMarkerKind.QuarantineDirectory
                || faultPoint != OwnershipCleanupFaultPoint.BeforeDirectoryDelete)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }
}
