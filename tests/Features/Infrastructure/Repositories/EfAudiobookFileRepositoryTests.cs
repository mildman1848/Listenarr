using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories;

[Trait("Name", "EfAudiobookFileRepositoryTests")]
[Trait("Category", "Persistence")]
public sealed class EfAudiobookFileRepositoryTests : BaseTests
{
    [Fact]
    public async Task UpdateAsync_TrackedMetadataChange_DoesNotOverwriteNewerPath()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var fileId = await SeedFileAsync(options);

        await using var metadataContext = new ListenArrDbContext(options);
        var repository = new EfAudiobookFileRepository(metadataContext);
        var staleFile = await repository.GetByIdAsync(fileId);
        Assert.NotNull(staleFile);

        await MoveFileReferenceAsync(options, fileId);

        staleFile!.DurationSeconds = 123;
        await repository.UpdateAsync(staleFile);

        await using var verification = new ListenArrDbContext(options);
        var persisted = await verification.AudiobookFiles.SingleAsync(file => file.Id == fileId);
        Assert.Equal("/library/target/book.m4b", persisted.Path);
        Assert.Equal(123, persisted.DurationSeconds);
    }

    [Fact]
    public async Task UpdateAsync_DetachedMetadataChange_DoesNotOverwriteNewerPath()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var fileId = await SeedFileAsync(options);
        AudiobookFile staleFile;

        await using (var staleContext = new ListenArrDbContext(options))
        {
            staleFile = await staleContext.AudiobookFiles
                .AsNoTracking()
                .SingleAsync(file => file.Id == fileId);
        }

        await MoveFileReferenceAsync(options, fileId);
        staleFile.DurationSeconds = 456;

        await using (var metadataContext = new ListenArrDbContext(options))
        {
            var repository = new EfAudiobookFileRepository(metadataContext);
            await repository.UpdateAsync(staleFile);
        }

        await using var verification = new ListenArrDbContext(options);
        var persisted = await verification.AudiobookFiles.SingleAsync(file => file.Id == fileId);
        Assert.Equal("/library/target/book.m4b", persisted.Path);
        Assert.Equal(456, persisted.DurationSeconds);
    }

    private static async Task<int> SeedFileAsync(DbContextOptions<ListenArrDbContext> options)
    {
        await using var context = new ListenArrDbContext(options);
        var audiobook = new Audiobook
        {
            Title = "Repository File",
            BasePath = "/library/source"
        };
        var file = new AudiobookFile
        {
            Audiobook = audiobook,
            Path = "/library/source/book.m4b"
        };
        context.AudiobookFiles.Add(file);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private static async Task MoveFileReferenceAsync(
        DbContextOptions<ListenArrDbContext> options,
        int fileId)
    {
        await using var context = new ListenArrDbContext(options);
        var file = await context.AudiobookFiles.SingleAsync(candidate => candidate.Id == fileId);
        file.Path = "/library/target/book.m4b";
        await context.SaveChangesAsync();
    }
}
