using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverDirectoryPublicationRaceTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverDirectoryPublicationRaceTests : BaseTests
{
    [Fact]
    public async Task CopyDirectoryAsync_ConflictingExpectedFileAppearsAfterPreflight_IsPreservedAndBlocked()
    {
        var root = FileService.GetTempDirectory("directory-copy-conflict-race");
        var source = Path.Join(root, "source");
        var destination = Path.Join(root, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "source-audio");
        var foreignPath = Path.Join(destination, "book.m4b");
        var mover = CreateMover(async () =>
        {
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(foreignPath, "foreign-audio");
        });

        var result = await mover.CopyDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal("foreign-audio", await File.ReadAllTextAsync(foreignPath));
        Assert.Equal("source-audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task CopyDirectoryAsync_UnexpectedSiblingAppearsAfterPreflight_IsPreservedAndBlocked()
    {
        var root = FileService.GetTempDirectory("directory-copy-extra-race");
        var source = Path.Join(root, "source");
        var destination = Path.Join(root, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "source-audio");
        var foreignPath = Path.Join(destination, "foreign.txt");
        var mover = CreateMover(async () =>
        {
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(foreignPath, "foreign");
        });

        var result = await mover.CopyDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignPath));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    private static FileMover CreateMover(Func<Task> afterPreflight) => new(
        new NullLogger<FileMover>(),
        options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
        semanticsResolver: new FileSystemSemanticsResolver())
    {
        AfterDirectoryCopyPreflightForTestAsync = afterPreflight
    };
}
