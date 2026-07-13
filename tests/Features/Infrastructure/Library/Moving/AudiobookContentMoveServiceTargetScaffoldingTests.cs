using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_MissingNestedTargetAncestors_AreNotCopiedAsContent()
    {
        var source = FileService.GetTempDirectory("content-move-nested-scaffold-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(source, "container", "nested", "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(Directory.Exists(Path.Join(target, "container")));
        Assert.False(Directory.Exists(Path.Join(target, "nested")));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var scaffolding = await db.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == request.JobId)
            .OrderBy(directory => directory.Path)
            .ToListAsync();
        Assert.Equal(2, scaffolding.Count);
        Assert.All(scaffolding, directory =>
            Assert.Equal(MoveCreatedDirectoryState.Created, directory.State));
    }

    [Fact]
    public async Task MoveContentsAsync_RetryAfterRemovedScaffolding_ReacquiresAndRetainsLedger()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-retry-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var firstScaffold = Path.Join(source, "container");
        var secondScaffold = Path.Join(firstScaffold, "nested");
        var target = Path.Join(secondScaffold, "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveJobCreatedDirectories.AddRange(
                new MoveJobCreatedDirectory
                {
                    MoveJobId = request.JobId,
                    Path = firstScaffold,
                    State = MoveCreatedDirectoryState.Removed
                },
                new MoveJobCreatedDirectory
                {
                    MoveJobId = request.JobId,
                    Path = secondScaffold,
                    State = MoveCreatedDirectoryState.Removed
                });
            await db.SaveChangesAsync();
        }
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        await service.MoveContentsAsync(request, CancellationToken.None);
        await service.RetainTargetScaffoldingAsync(request, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(File.Exists(Path.Join(firstScaffold, ".listenarr-scaffold-owner.json")));
        await using var verification = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var scaffolding = await verification.MoveJobCreatedDirectories
            .AsNoTracking()
            .Where(directory => directory.MoveJobId == request.JobId)
            .ToListAsync();
        Assert.Equal(2, scaffolding.Count);
        Assert.All(scaffolding, directory =>
            Assert.Equal(MoveCreatedDirectoryState.Retained, directory.State));
    }

    [Fact]
    public async Task MoveContentsAsync_PersistedScaffoldWithUnexpectedContent_FailsClosed()
    {
        var source = FileService.GetTempDirectory("content-move-scaffold-content-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var scaffold = Path.Join(source, "container");
        var target = Path.Join(scaffold, "target");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveJobCreatedDirectories.Add(new MoveJobCreatedDirectory
            {
                MoveJobId = request.JobId,
                Path = scaffold,
                State = MoveCreatedDirectoryState.Planned
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(scaffold);
        await File.WriteAllTextAsync(Path.Join(scaffold, "operator-note.txt"), "keep me");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("unexpected content", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(scaffold, "operator-note.txt")));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }
}
