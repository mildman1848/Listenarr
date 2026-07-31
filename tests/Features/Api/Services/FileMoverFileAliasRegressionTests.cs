using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

public sealed class FileLinkTheoryAttribute : TheoryAttribute
{
    public FileLinkTheoryAttribute()
    {
        if (DirectoryLinkTestsAreRequired() || CanCreateFileLink())
        {
            return;
        }

        Skip = "File symbolic links are unavailable on this test runner.";
    }

    private static bool CanCreateFileLink()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-file-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target");
        var link = Path.Join(root, "link");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "capability");
            File.CreateSymbolicLink(link, target);
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            TryDeleteCapabilityTree(root, link);
        }
    }

    internal static bool DirectoryLinkTestsAreRequired() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "LISTENARR_REQUIRE_DIRECTORY_LINK_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    internal static void TryDeleteCapabilityTree(string root, string link)
    {
        try
        {
            if (File.Exists(link)
                && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            {
                File.Delete(link);
            }
            else if (Directory.Exists(link)
                && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            // Best effort discovery-time cleanup.
        }
    }
}

public sealed class DirectoryLinkTheoryAttribute : TheoryAttribute
{
    public DirectoryLinkTheoryAttribute()
    {
        if (FileLinkTheoryAttribute.DirectoryLinkTestsAreRequired()
            || CanCreateDirectoryLink())
        {
            return;
        }

        Skip = "Directory symbolic links are unavailable on this test runner.";
    }

    private static bool CanCreateDirectoryLink()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-directory-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target");
        var link = Path.Join(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            FileLinkTheoryAttribute.TryDeleteCapabilityTree(root, link);
        }
    }
}

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverFileAliasRegressionTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverFileAliasRegressionTests : BaseTests
{
    [FileLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationSymlinkAlias_IsBlocked(FileAction action)
    {
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

    [DirectoryLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SymbolicLinkAncestorAlias_IsBlocked(FileAction action)
    {
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

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationSymlinkToUnrelatedFile_IsBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-unrelated-link-destination");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(destination, external);

        var result = await PerformAsync(CreateMover(), action, source, destination);

        Assert.False(result);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
    }

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SourceSymlinkToUnrelatedFile_IsBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-unrelated-link-source");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        File.CreateSymbolicLink(source, external);

        var result = await PerformAsync(CreateMover(), action, source, destination);

        Assert.False(result);
        Assert.Equal("external", await File.ReadAllTextAsync(external));
        Assert.False(File.Exists(destination));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationLinkedAncestor_IsBlockedBeforeCreatingChildren(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-linked-ancestor-external");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var external = Path.Join(root, "external");
        Directory.CreateDirectory(external);
        var linkedParent = Path.Join(root, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, external);

        var destination = Path.Join(linkedParent, "nested", "destination.m4b");
        var result = await PerformAsync(CreateMover(), action, source, destination);

        Assert.False(result);
        Assert.False(Directory.Exists(Path.Join(external, "nested")));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_LinkedLockDirectoryAncestor_DoesNotCreateOutsideBoundary(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-lock-linked-ancestor");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        var external = Path.Join(root, "external-lock-root");
        Directory.CreateDirectory(external);
        var linkedParent = Path.Join(root, "linked-lock-parent");
        Directory.CreateSymbolicLink(linkedParent, external);
        var lockDirectory = Path.Join(linkedParent, "file-move-locks");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            FileMoveLockDirectoryForTest = lockDirectory
        };

        var result = await PerformAsync(
            mover,
            action,
            source,
            destination);

        Assert.False(result);
        Assert.False(Directory.Exists(Path.Join(external, "file-move-locks")));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
    }

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationLeafReplacedAfterPinning_IsPreservedAndBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-leaf-race-destination");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = await FileService.GetFileAsync(root, "destination.m4b", "old");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterFileEntriesPinnedForTestAsync = (_, _, _) =>
            {
                File.Delete(destination);
                File.CreateSymbolicLink(destination, external);
                return Task.CompletedTask;
            }
        };

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.Equal("external", await File.ReadAllTextAsync(external));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [FileLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SourceLeafReplacedAfterPinning_IsPreservedAndBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-leaf-race-source");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var original = Path.Join(root, "source.original.m4b");
        var destination = Path.Join(root, "destination.m4b");
        var external = await FileService.GetFileAsync(root, "external.m4b", "external");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterFileEntriesPinnedForTestAsync = (_, _, _) =>
            {
                File.Move(source, original);
                File.CreateSymbolicLink(source, external);
                return Task.CompletedTask;
            }
        };

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.Equal("audio", await File.ReadAllTextAsync(original));
        Assert.Equal("external", await File.ReadAllTextAsync(external));
        Assert.False(File.Exists(destination));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_DestinationAncestorReplacedAfterPinning_IsPreservedAndBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-ancestor-race-destination");
        var sourceParent = Path.Join(root, "source-parent");
        var destinationParent = Path.Join(root, "destination-parent");
        var originalDestinationParent = Path.Join(root, "destination-parent.original");
        var external = Path.Join(root, "external");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        Directory.CreateDirectory(external);
        var source = await FileService.GetFileAsync(sourceParent, "source.m4b", "audio");
        var destination = Path.Join(destinationParent, "destination.m4b");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterFileEndpointsPinnedForTestAsync = (_, _, _) =>
            {
                Directory.Move(destinationParent, originalDestinationParent);
                Directory.CreateSymbolicLink(destinationParent, external);
                return Task.CompletedTask;
            }
        };

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [DirectoryLinkTheory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SourceAncestorReplacedAfterPinning_IsPreservedAndBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-ancestor-race-source");
        var sourceParent = Path.Join(root, "source-parent");
        var originalSourceParent = Path.Join(root, "source-parent.original");
        var destinationParent = Path.Join(root, "destination-parent");
        var external = Path.Join(root, "external");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        Directory.CreateDirectory(external);
        var source = await FileService.GetFileAsync(sourceParent, "source.m4b", "audio");
        var externalSource = await FileService.GetFileAsync(
            external,
            "source.m4b",
            "external");
        var destination = Path.Join(destinationParent, "destination.m4b");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterFileEndpointsPinnedForTestAsync = (_, _, _) =>
            {
                Directory.Move(sourceParent, originalSourceParent);
                Directory.CreateSymbolicLink(sourceParent, external);
                return Task.CompletedTask;
            }
        };

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.Equal("external", await File.ReadAllTextAsync(externalSource));
        Assert.Equal(
            "audio",
            await File.ReadAllTextAsync(
                Path.Join(originalSourceParent, "source.m4b")));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task FileOperation_SourceBytesChangeAfterCapture_IsBlocked(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("file-content-race-source");
        var source = await FileService.GetFileAsync(root, "source.m4b", "audio");
        var destination = Path.Join(root, "destination.m4b");
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterPinnedSourceContentCapturedForTestAsync = (_, _, _) =>
            {
                File.WriteAllText(source, "changed");
                return Task.CompletedTask;
            }
        };

        var result = await PerformAsync(mover, action, source, destination);

        Assert.False(result);
        Assert.Equal("changed", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
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
