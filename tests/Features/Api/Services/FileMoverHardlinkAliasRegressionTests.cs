using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverHardlinkAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverHardlinkAliasRegressionTests : BaseTests
{
    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationHardlinkAlias_IsBlocked(FileAction action)
    {
        var root = FileService.GetTempDirectory("file-hardlink-alias");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        if (!TryCreateHardLink(destination, source))
        {
            return;
        }
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver());

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(destination));
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

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            File.CreateHardLink(linkPath, existingPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
