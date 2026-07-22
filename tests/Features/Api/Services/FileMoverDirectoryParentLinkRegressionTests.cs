using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverDirectoryParentLinkRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverDirectoryParentLinkRegressionTests : BaseTests
{
    [Fact]
    public async Task MoveDirectoryAsync_LinkedDestinationAncestor_IsRejectedBeforeFallbackWrites()
    {
        var root = FileService.GetTempDirectory("directory-parent-link");
        var source = Path.Join(root, "source");
        var physicalRoot = Path.Join(root, "physical-root");
        var physicalParent = Path.Join(physicalRoot, "parent");
        var linkedRoot = Path.Join(root, "linked-root");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(physicalParent);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, physicalRoot);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var destination = Path.Join(linkedRoot, "parent", "book");
        var physicalDestination = Path.Join(physicalParent, "book");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            BeforeDirectoryMoveAttemptForTest = () =>
                throw new IOException("Force the verified directory fallback.")
        };

        var result = await mover.MoveDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        Assert.False(Directory.Exists(destination));
        Assert.False(Directory.Exists(physicalDestination));
    }
}
