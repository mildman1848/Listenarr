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
        var mover = CreateMover(afterPreflight: async () =>
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
        var mover = CreateMover(afterPreflight: async () =>
        {
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(foreignPath, "foreign");
        });

        var result = await mover.CopyDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignPath));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [LinuxFact]
    public async Task CopyDirectoryAsync_NestedStagingDirectoryReplacedBeforeFileCopy_DoesNotWriteThroughLink()
    {
        var root = FileService.GetTempDirectory("directory-copy-nested-staging-race");
        var source = Path.Join(root, "source");
        var destination = Path.Join(root, "destination");
        var sourceNested = Path.Join(source, "nested");
        var external = FileService.GetTempDirectory("directory-copy-nested-staging-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(sourceNested);
        await File.WriteAllTextAsync(Path.Join(sourceNested, "book.m4b"), "source-audio");
        Assert.True(
            TryCreateDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        string? stagingRoot = null;
        string? displacedNested = null;
        var hookRan = false;
        var mover = CreateMover(afterStagingDirectoriesCreated: path =>
        {
            stagingRoot = path;
            var stagingNested = Path.Join(path, "nested");
            displacedNested = stagingNested + ".original";
            Directory.Move(stagingNested, displacedNested);
            Directory.CreateSymbolicLink(stagingNested, external);
            hookRan = true;
            return Task.CompletedTask;
        });

        try
        {
            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(hookRan);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.Equal("source-audio", await File.ReadAllTextAsync(Path.Join(sourceNested, "book.m4b")));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (stagingRoot != null)
            {
                var stagingNested = Path.Join(stagingRoot, "nested");
                TryDeleteDirectoryLink(stagingNested);
                if (displacedNested != null
                    && Directory.Exists(displacedNested)
                    && !Directory.Exists(stagingNested))
                {
                    Directory.Move(displacedNested, stagingNested);
                }
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
        }
    }

    [LinuxFact]
    public async Task CopyDirectoryAsync_StagingRootReplacedBeforePublication_IsBlocked()
    {
        var root = FileService.GetTempDirectory("directory-copy-publication-race");
        var source = Path.Join(root, "source");
        var destination = Path.Join(root, "destination");
        var external = FileService.GetTempDirectory("directory-copy-publication-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "source-audio");
        Assert.True(
            TryCreateDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        string? stagingRoot = null;
        string? displacedStaging = null;
        var hookRan = false;
        var mover = CreateMover(beforePublication: path =>
        {
            stagingRoot = path;
            displacedStaging = path + ".original";
            Directory.Move(path, displacedStaging);
            Directory.CreateSymbolicLink(path, external);
            hookRan = true;
            return Task.CompletedTask;
        });

        try
        {
            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(hookRan);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.False(Directory.Exists(destination));
            Assert.Equal("source-audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }
        finally
        {
            if (stagingRoot != null)
            {
                TryDeleteDirectoryLink(stagingRoot);
                if (displacedStaging != null
                    && Directory.Exists(displacedStaging)
                    && !Directory.Exists(stagingRoot))
                {
                    Directory.Move(displacedStaging, stagingRoot);
                }
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task CopyDirectoryAsync_StagingFileReplacedBeforeCleanup_PreservesReplacement()
    {
        var root = FileService.GetTempDirectory("directory-copy-cleanup-race");
        var source = Path.Join(root, "source");
        var destination = Path.Join(root, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "source-audio");
        string? stagingRoot = null;
        string? displacedFile = null;
        var mover = CreateMover(
            beforePublication: _ =>
                throw new IOException("Force staging cleanup."),
            beforeStagingCleanup: async path =>
            {
                stagingRoot = path;
                var stagingFile = Path.Join(path, "book.m4b");
                displacedFile = stagingFile + ".validated";
                File.Move(stagingFile, displacedFile);
                await File.WriteAllTextAsync(stagingFile, "replacement");
            });

        try
        {
            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.NotNull(stagingRoot);
            Assert.NotNull(displacedFile);
            Assert.Equal(
                "replacement",
                await File.ReadAllTextAsync(Path.Join(stagingRoot!, "book.m4b")));
            Assert.Equal("source-audio", await File.ReadAllTextAsync(displacedFile!));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (stagingRoot != null && Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static FileMover CreateMover(
        Func<Task>? afterPreflight = null,
        Func<string, Task>? afterStagingDirectoriesCreated = null,
        Func<string, Task>? beforePublication = null,
        Func<string, Task>? beforeStagingCleanup = null) => new(
        new NullLogger<FileMover>(),
        options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
        semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterDirectoryCopyPreflightForTestAsync = afterPreflight,
            AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync = afterStagingDirectoriesCreated,
            BeforeDirectoryCopyPublicationForTestAsync = beforePublication,
            BeforeDirectoryCopyStagingCleanupForTestAsync = beforeStagingCleanup
        };

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup under the per-test temporary root.
        }
    }
}
