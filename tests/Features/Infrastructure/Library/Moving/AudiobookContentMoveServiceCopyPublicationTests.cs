using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_PartialChangesBeforePublication_PreservesSourceAndBlocksDestination()
    {
        var source = FileService.GetTempDirectory("content-move-partial-publish-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(source, "published");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var partialPath = Path.Join(
            target,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ReplacePartialBeforePublication(partialPath));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("changed before publication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(partialPath));
        Assert.Equal("corrupted audio", await File.ReadAllTextAsync(partialPath));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(partialPath + ".replaced"));
    }

    [Fact]
    public async Task MoveContentsAsync_TransientCopyFailure_PreservesVerifiedTempProgressForRetry()
    {
        var source = FileService.GetTempDirectory("content-move-temp-progress-src");
        await FileService.GetFileAsync(source, "first.m4b", "first audio");
        await FileService.GetFileAsync(source, "second.m4b", "second audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-progress-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailCopiesAfterOneFileCompletes(tempDirectory));

        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));

        Assert.IsNotType<MoveNeedsAttentionException>(exception);
        Assert.True(Directory.Exists(tempDirectory));
        Assert.True(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
        Assert.Single(Directory.EnumerateFiles(tempDirectory, "*.m4b", SearchOption.TopDirectoryOnly));
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(tempDirectory));
        Assert.False(Directory.Exists(source));
        Assert.Equal("first audio", await File.ReadAllTextAsync(Path.Join(target, "first.m4b")));
        Assert.Equal("second audio", await File.ReadAllTextAsync(Path.Join(target, "second.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_TargetAppearsAtTempPublicationBoundary_PreservesOperatorContent()
    {
        var source = FileService.GetTempDirectory("content-move-temp-target-race-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-target-race-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new CreateTargetBeforeTempPublication(target));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("target appeared", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.Equal(
            "preserve me",
            await File.ReadAllTextAsync(Path.Join(target, "operator-note.txt")));
        Assert.False(Directory.Exists(tempDirectory));
    }

    private sealed class CreateTargetBeforeTempPublication(string target) : IMoveFaultInjector
    {
        private bool _created;

        public void OnTempPublication(Guid jobId, TempPublicationFaultPoint faultPoint)
        {
            if (_created || faultPoint != TempPublicationFaultPoint.BeforeFinalValidation)
            {
                return;
            }

            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Join(target, "operator-note.txt"), "preserve me");
            _created = true;
        }
    }

    private sealed class FailCopiesAfterOneFileCompletes(string tempDirectory) : IMoveFaultInjector
    {
        public void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
        {
            if (faultPoint == CopyMutationFaultPoint.AfterChunkWritten
                && Directory.Exists(tempDirectory)
                && Directory.EnumerateFiles(
                    tempDirectory,
                    "*.m4b",
                    SearchOption.TopDirectoryOnly).Any())
            {
                throw new IOException("Simulated transient copy interruption.");
            }
        }
    }

    private sealed class ReplacePartialBeforePublication(string partialPath) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != CopyMutationFaultPoint.BeforePartialPublication)
            {
                return;
            }

            File.Move(partialPath, partialPath + ".replaced");
            File.WriteAllText(partialPath, "corrupted audio");
            _replaced = true;
        }
    }
}
