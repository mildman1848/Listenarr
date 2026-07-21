using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFileAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFileAliasRegressionTests : BaseTests
{
    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationSymlinkAlias_IsBlocked(FileAction action)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = FileService.GetTempDirectory("file-alias-leaf");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(destination, source);
        var mover = CreateMover();

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SymbolicLinkAncestorAlias_IsBlocked(FileAction action)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = FileService.GetTempDirectory("file-alias-ancestor");
        var physicalParent = Path.Join(root, "physical");
        Directory.CreateDirectory(physicalParent);
        var source = await FileService.GetFileAsync(physicalParent, "book.m4b", "audio");
        var aliasParent = Path.Join(root, "alias");
        Directory.CreateSymbolicLink(aliasParent, physicalParent);
        var destination = Path.Join(aliasParent, "book.m4b");
        var mover = CreateMover();

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_LiteralSamePath_RemainsIdempotent(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-alias-identical");
        var source = await FileService.GetFileAsync(root, "book.m4b", "audio");
        var mover = CreateMover();

        var result = await PerformAsync(mover, action, source, source);

        Assert.True(result);
        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    private static Task<bool> PerformAsync(
        FileMover mover,
        FileAction action,
        string source,
        string destination) => action switch
        {
            FileAction.Move => mover.MoveFileAsync(source, destination),
            FileAction.Copy => mover.CopyFileAsync(source, destination),
            FileAction.HardlinkCopy => mover.HardlinkFileAsync(source, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

    private static FileMover CreateMover() => new(
        new NullLogger<FileMover>(),
        options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
        semanticsResolver: new FileSystemSemanticsResolver());
}
