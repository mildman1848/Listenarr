using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

public sealed class MoveScanHandoffRecoveryServiceTests : BaseTests
{
    [Fact]
    public async Task RecoverAsync_PendingMoveScanHandoff_ClaimsAndEnqueuesAttempt()
    {
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Recover Pending Move Scan",
            BasePath = FileService.GetTempDirectory("move-scan-handoff-pending")
        });
        var handoff = await InsertHandoffAsync(
            audiobook.Id,
            audiobook.BasePath!,
            MoveScanHandoffStatus.Pending);
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var scanQueue = new ScanQueueService(
            NullLogger<ScanQueueService>.Instance,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            store,
            TimeProvider.System);
        var service = new MoveScanHandoffRecoveryService(
            scanQueue,
            store,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);

        await service.RecoverAsync(CancellationToken.None);

        Assert.True(scanQueue.Reader.TryRead(out var scanJob));
        Assert.Equal(handoff.Id, scanJob.MoveScanHandoffId);
        Assert.Equal(1, scanJob.MoveScanAttemptGeneration);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Claimed, persisted.Status);
        Assert.Equal(scanJob.Id, persisted.ActiveScanJobId);
    }

    [Fact]
    public async Task RecoverAsync_HandoffWithoutAudiobook_RecordsTerminalFailure()
    {
        var handoff = await InsertHandoffAsync(
            audiobookId: 987654,
            FileService.GetTempDirectory("move-scan-handoff-missing"),
            MoveScanHandoffStatus.Pending);
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var service = new MoveScanHandoffRecoveryService(
            scanQueue.Object,
            _provider.GetRequiredService<IMoveScanHandoffStore>(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);

        await service.RecoverAsync(CancellationToken.None);

        scanQueue.VerifyNoOtherCalls();
        await using var db = await GetFactory().CreateDbContextAsync();
        var persisted = await db.MoveScanHandoffs.AsNoTracking().SingleAsync(candidate => candidate.Id == handoff.Id);
        Assert.Equal(MoveScanHandoffStatus.Failed, persisted.Status);
        Assert.Contains("no longer exists", persisted.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await db.History.AsNoTracking()
            .Where(history => history.EventType == HistoryEvents.ScanFailed
                && history.IdempotencyKey != null
                && history.IdempotencyKey.StartsWith($"handoff:{handoff.Id:N}:"))
            .ToListAsync());
    }

    [Fact]
    public async Task RecoverAsync_UnrecoverableHandoff_DoesNotPoisonLaterDispatch()
    {
        var missing = await InsertHandoffAsync(
            audiobookId: 987655,
            FileService.GetTempDirectory("move-scan-handoff-poison-missing"),
            MoveScanHandoffStatus.Pending);
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Recovery continues",
            BasePath = FileService.GetTempDirectory("move-scan-handoff-poison-valid")
        });
        var valid = await InsertHandoffAsync(
            audiobook.Id,
            audiobook.BasePath!,
            MoveScanHandoffStatus.Pending);
        var store = _provider.GetRequiredService<IMoveScanHandoffStore>();
        var scanQueue = new ScanQueueService(
            NullLogger<ScanQueueService>.Instance,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            store,
            TimeProvider.System);
        var service = new MoveScanHandoffRecoveryService(
            scanQueue,
            store,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);

        await service.RecoverAsync(CancellationToken.None);

        Assert.True(scanQueue.Reader.TryRead(out var scanJob));
        Assert.Equal(valid.Id, scanJob.MoveScanHandoffId);
        await using var db = await GetFactory().CreateDbContextAsync();
        var persistedMissing = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == missing.Id);
        var persistedValid = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == valid.Id);
        Assert.Equal(MoveScanHandoffStatus.Failed, persistedMissing.Status);
        Assert.Equal(MoveScanHandoffStatus.Claimed, persistedValid.Status);
    }

    [Theory]
    [InlineData(MoveScanHandoffStatus.Succeeded)]
    [InlineData(MoveScanHandoffStatus.Failed)]
    public async Task RecoverAsync_TerminalMoveScanHandoff_DoesNotEnqueue(
        MoveScanHandoffStatus status)
    {
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Terminal Move Scan",
            BasePath = FileService.GetTempDirectory("move-scan-handoff-terminal")
        });
        await InsertHandoffAsync(audiobook.Id, audiobook.BasePath!, status);
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var service = new MoveScanHandoffRecoveryService(
            scanQueue.Object,
            _provider.GetRequiredService<IMoveScanHandoffStore>(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MoveScanHandoffRecoveryService>.Instance);

        await service.RecoverAsync(CancellationToken.None);

        scanQueue.VerifyNoOtherCalls();
    }

    private IDbContextFactory<ListenArrDbContext> GetFactory() =>
        _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();

    private async Task<MoveScanHandoff> InsertHandoffAsync(
        int audiobookId,
        string targetPath,
        MoveScanHandoffStatus status)
    {
        await using var db = await GetFactory().CreateDbContextAsync();
        var resolution = await _provider
            .GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(targetPath);
        Assert.Equal(PathIdentityState.Valid, resolution.State);
        var identity = PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            resolution.BoundaryPath,
            targetPath);
        var moveJob = new MoveJob
        {
            AudiobookId = audiobookId,
            RequestedPath = targetPath,
            SourcePath = targetPath,
            Status = MoveJobStatus.Completed,
            Phase = MoveJobPhase.RecordingCompletion,
            IdentityKeyVersion = 3
        };
        moveJob.SetSourceIdentity(identity);
        moveJob.SetTargetIdentity(identity);
        var handoff = new MoveScanHandoff
        {
            MoveJobId = moveJob.Id,
            AudiobookId = audiobookId,
            TargetPath = targetPath,
            Status = status
        };
        db.MoveJobs.Add(moveJob);
        db.MoveScanHandoffs.Add(handoff);
        await db.SaveChangesAsync();
        return handoff;
    }
}
