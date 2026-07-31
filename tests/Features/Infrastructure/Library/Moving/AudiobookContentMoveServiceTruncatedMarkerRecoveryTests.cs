using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_TruncatedPredecessorRecoveryWrite_IsDiscardedSafely()
    {
        var source = FileService.GetTempDirectory("content-move-truncated-recovery-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-truncated-recovery-dst-{Guid.NewGuid():N}");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);
        var markerPath = Path.Join(
            source,
            $".listenarr-move-{request.JobId:N}.pending");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            request.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(writePath, "{\"Version\":1");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(File.Exists(writePath));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_PredecessorRecoveryWriteReplacedBeforeDeletion_PreservesReplacement()
    {
        var source = FileService.GetTempDirectory("content-move-replaced-recovery-write-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-replaced-recovery-write-dst-{Guid.NewGuid():N}");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);
        var markerPath = Path.Join(
            source,
            $".listenarr-move-{request.JobId:N}.pending");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            request.JobId,
            initialRequest.LeaseGeneration);
        var displacedPath = writePath + ".validated";
        await File.WriteAllTextAsync(writePath, "{\"Version\":1");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ReplaceRecoveryWriteBeforeDeletion(writePath, displacedPath));

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Equal("replacement", await File.ReadAllTextAsync(writePath));
        Assert.True(File.Exists(displacedPath));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_CompletePredecessorRecoveryWrite_IsPublished()
    {
        var source = FileService.GetTempDirectory("content-move-complete-recovery-write-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-complete-recovery-write-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(initialRequest.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{initialRequest.JobId:N}.pending");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            initialRequest.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(
            writePath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = initialRequest.JobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-complete"
            }));
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(
            request,
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.False(recovered!.SourceCleanupCompleted);
        Assert.True(File.Exists(markerPath));
        Assert.False(File.Exists(writePath));
        Assert.Contains("copy-complete", await File.ReadAllTextAsync(markerPath));

        var completed = await service.ResumeSourceCleanupAsync(
            request,
            recovered,
            CancellationToken.None);
        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_MultipleCompatiblePredecessorWrites_UsesLatestStage()
    {
        var source = FileService.GetTempDirectory("content-move-multiple-writes-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-multiple-writes-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(initialRequest.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{initialRequest.JobId:N}.pending");
        var startedWrite = CreateTruncatedMarkerWritePath(
            markerPath,
            initialRequest.JobId,
            initialRequest.LeaseGeneration);
        var completedWrite = CreateTruncatedMarkerWritePath(
            markerPath,
            initialRequest.JobId,
            initialRequest.LeaseGeneration);
        await WriteStructuredRecoveryWriteAsync(
            startedWrite,
            initialRequest,
            "copy-started");
        await WriteStructuredRecoveryWriteAsync(
            completedWrite,
            initialRequest,
            "copy-complete");
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(
            request,
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.False(recovered!.SourceCleanupCompleted);
        Assert.False(File.Exists(startedWrite));
        Assert.False(File.Exists(completedWrite));
        Assert.Contains("copy-complete", await File.ReadAllTextAsync(markerPath));
    }

    [Fact]
    public async Task MoveContentsAsync_CompleteTempRecoveryWrite_IsPublishedAndResumed()
    {
        var source = FileService.GetTempDirectory("content-move-complete-temp-write-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-complete-temp-write-dst-{Guid.NewGuid():N}");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new StopBeforeRecoveryMarkerPublication());

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            faultingService.MoveContentsAsync(initialRequest, CancellationToken.None));

        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + initialRequest.JobId.ToString("N"));
        var markerPath = Path.Join(
            tempDirectory,
            $".listenarr-move-{initialRequest.JobId:N}.pending");
        var writePath = Assert.Single(Directory.EnumerateFiles(
            tempDirectory,
            Path.GetFileName(markerPath) + ".writing-*"));
        Assert.False(File.Exists(markerPath));
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(File.Exists(writePath));
        Assert.False(Directory.Exists(tempDirectory));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_UnownedTempRecoveryWrite_IsPreservedWithoutPublication()
    {
        var source = FileService.GetTempDirectory("content-move-unowned-temp-write-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-unowned-temp-write-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var markerPath = Path.Join(
            tempDirectory,
            $".listenarr-move-{request.JobId:N}.pending");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            request.JobId,
            request.LeaseGeneration);
        await WriteStructuredRecoveryWriteAsync(
            writePath,
            request,
            "copy-started");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(File.Exists(writePath));
        Assert.False(File.Exists(markerPath));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_TruncatedPredecessorTempOwnershipWrite_ReclaimsEmptyDirectory()
    {
        var source = FileService.GetTempDirectory("content-move-truncated-temp-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-truncated-temp-dst-{Guid.NewGuid():N}");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var markerPath = Path.Join(tempDirectory, ".listenarr-temp-owner.json");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            request.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(writePath, "{\"Version\":1");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(tempDirectory));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_TruncatedPredecessorQuarantineOwnershipWrite_ReclaimsEmptyDirectory()
    {
        var source = FileService.GetTempDirectory("content-move-truncated-quarantine-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-truncated-quarantine-dst");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{request.JobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        var markerPath = Path.Join(
            quarantineRoot,
            ".listenarr-quarantine-owner.json");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            request.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(writePath, "{\"Version\":1");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ResumeSourceCleanup_TruncatedPredecessorTombstoneWrite_IsRepublished()
    {
        var source = FileService.GetTempDirectory("content-move-truncated-tombstone-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-truncated-tombstone-dst");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new StopBeforeTombstonePublication());
        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            faultingService.MoveContentsAsync(initialRequest, CancellationToken.None));

        var sourceParent = Path.GetDirectoryName(source)!;
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{initialRequest.JobId:N}");
        var tombstonePath = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{initialRequest.JobId:N}.cleanup.json");
        Assert.True(File.Exists(Path.Join(
            quarantineRoot,
            ".listenarr-quarantine-owner.json")));
        Assert.False(File.Exists(tombstonePath));
        var writePath = CreateTruncatedMarkerWritePath(
            tombstonePath,
            initialRequest.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(writePath, "{\"Version\":1");
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(
            request,
            CancellationToken.None);
        Assert.NotNull(recovered);
        var completed = await service.ResumeSourceCleanupAsync(
            request,
            recovered!,
            CancellationToken.None);

        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.False(File.Exists(tombstonePath));
        Assert.False(File.Exists(writePath));
    }

    [WindowsFact]
    public async Task GetRecoverableMoveAsync_LockedAuthoritativeMarker_IsRetryableAndPreserved()
    {

        var source = FileService.GetTempDirectory("content-move-locked-marker-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-locked-marker-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(request.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{request.JobId:N}.pending");
        await File.WriteAllTextAsync(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = request.JobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-complete"
            }));
        await using var lockStream = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.GetRecoverableMoveAsync(request, CancellationToken.None));

        Assert.Contains("temporarily unreadable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_OversizedAuthoritativeMarker_IsPreservedForReview()
    {
        var source = FileService.GetTempDirectory("content-move-oversized-marker-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-oversized-marker-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(request.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{request.JobId:N}.pending");
        await File.WriteAllTextAsync(markerPath, new string('x', 70 * 1024));
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request, CancellationToken.None));

        Assert.Contains("supported size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_UnsupportedAuthoritativeMarker_IsPreservedForReview()
    {
        var source = FileService.GetTempDirectory("content-move-unsupported-marker-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-unsupported-marker-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(request.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{request.JobId:N}.pending");
        await File.WriteAllTextAsync(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 2,
                JobId = request.JobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-complete"
            }));
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request, CancellationToken.None));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_UnsupportedPredecessorWrite_IsPreservedForReview()
    {
        var source = FileService.GetTempDirectory("content-move-unsupported-write-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-unsupported-write-dst");
        await FileService.GetFileAsync(target, "book.m4b", "audio");
        var initialRequest = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(initialRequest.JobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(
            target,
            $".listenarr-move-{initialRequest.JobId:N}.pending");
        var writePath = CreateTruncatedMarkerWritePath(
            markerPath,
            initialRequest.JobId,
            initialRequest.LeaseGeneration);
        await File.WriteAllTextAsync(
            writePath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 2,
                JobId = initialRequest.JobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-complete"
            }));
        var request = await ReplaceMarkerTestLeaseAsync(initialRequest);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request, CancellationToken.None));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(writePath));
    }

    private static Task WriteStructuredRecoveryWriteAsync(
        string writePath,
        AudiobookContentMoveRequest request,
        string stage) =>
        File.WriteAllTextAsync(
            writePath,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = request.JobId,
                Source = Path.GetFullPath(request.Source),
                Target = Path.GetFullPath(request.Target),
                Stage = stage
            }));

    private async Task<AudiobookContentMoveRequest> ReplaceMarkerTestLeaseAsync(
        AudiobookContentMoveRequest request)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var job = await db.MoveJobs.SingleAsync(candidate => candidate.Id == request.JobId);
        job.LeaseOwner = "replacement-marker-worker";
        job.LeaseGeneration++;
        job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        return request with
        {
            LeaseToken = new MoveLeaseToken(
                job.LeaseOwner,
                job.LeaseGeneration)
        };
    }

    private static string CreateTruncatedMarkerWritePath(
        string markerPath,
        Guid jobId,
        int leaseGeneration) =>
        markerPath
        + $".writing-{jobId:N}-g{leaseGeneration}-{Guid.NewGuid():N}";

    private sealed class ReplaceRecoveryWriteBeforeDeletion(
        string writePath,
        string displacedPath) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnRecoveryMarkerWrite(
            Guid jobId,
            RecoveryMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || faultPoint != RecoveryMarkerWriteFaultPoint.BeforeTemporaryFileDeletion)
            {
                return;
            }

            _replaced = true;
            File.Move(writePath, displacedPath);
            File.WriteAllText(writePath, "replacement");
        }
    }

    private sealed class StopBeforeRecoveryMarkerPublication : IMoveFaultInjector
    {
        private bool _failed;

        public void OnRecoveryMarkerWrite(
            Guid jobId,
            RecoveryMarkerWriteFaultPoint faultPoint)
        {
            if (_failed || faultPoint != RecoveryMarkerWriteFaultPoint.BeforePublication)
            {
                return;
            }

            _failed = true;
            throw new MoveLeaseLostException(jobId, 1);
        }
    }

    private sealed class StopBeforeTombstonePublication : IMoveFaultInjector
    {
        private bool _failed;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_failed
                || markerKind != OwnershipMarkerKind.CleanupTombstone
                || faultPoint != OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation)
            {
                return;
            }

            _failed = true;
            throw new MoveLeaseLostException(jobId, 1);
        }
    }
}
