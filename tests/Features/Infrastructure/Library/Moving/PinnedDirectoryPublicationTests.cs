using System.Security.Cryptography;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "PinnedDirectoryCreationTests")]
[Trait("Category", "Infrastructure")]
public sealed class PinnedDirectoryCreationTests : BaseTests
{
    [Fact]
    public void PublishCreatedDirectoryAs_EmptyDirectory_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-empty");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.False(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.True(Directory.Exists(Path.Join(parent, "published")));
        Assert.True(published.VisiblePathMatches());
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_NonEmptyHierarchyWithReleasedDescendants_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-released-hierarchy");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await creation.WriteInsideFileAsync("marker.json", "{}", CancellationToken.None);
        using (var rootAnchor = creation.OpenCreatedDirectoryAnchor())
        {
            using var childCreation = rootAnchor.TryCreateChild("child");
            Assert.True(childCreation.Created);
            using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();
            Assert.True(childAnchor.VisiblePathMatches());
        }

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.False(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.True(File.Exists(Path.Join(parent, "published", "marker.json")));
        Assert.True(Directory.Exists(Path.Join(parent, "published", "child")));
        Assert.True(published.VisiblePathMatches());
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_ExistingDestination_PreservesBothDirectories()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-collision");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await creation.WriteInsideFileAsync("prepared.txt", "prepared", CancellationToken.None);
        var published = Path.Join(parent, "published");
        Directory.CreateDirectory(published);
        await File.WriteAllTextAsync(Path.Join(published, "existing.txt"), "existing");

        Assert.ThrowsAny<Exception>(() =>
            creation.PublishCreatedDirectoryAs("published").Dispose());

        Assert.Equal("prepared", await File.ReadAllTextAsync(Path.Join(parent, "prepared", "prepared.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Join(published, "existing.txt")));
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_NonEmptyHierarchyWithLiveRootAnchor_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-live-root");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await creation.WriteInsideFileAsync("marker.json", "{}", CancellationToken.None);
        using var rootAnchor = creation.OpenCreatedDirectoryAnchor();
        using (var childCreation = rootAnchor.TryCreateChild("child"))
        {
            Assert.True(childCreation.Created);
            using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();
            Assert.True(childAnchor.VisiblePathMatches());
        }

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.True(rootAnchor.VisiblePathMatches(Path.Join(parent, "published")));
        Assert.True(published.VisiblePathMatches());
        Assert.True(Directory.Exists(Path.Join(parent, "published", "child")));
    }

    [Fact]
    public async Task MoveExistingFileTo_PublishesOpenedFileBetweenPinnedParents()
    {
        var sourceParent = FileService.GetTempDirectory("pinned-file-move-source");
        var destinationParent = FileService.GetTempDirectory("pinned-file-move-destination");
        var sourceFile = await FileService.GetFileAsync(sourceParent, "book.m4b", "verified audio");
        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile)));
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParent);
        using var destinationAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(destinationParent);
        using (var sourceEntry = sourceAnchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            Assert.True(await sourceEntry.MatchesAsync(
                new FileInfo(sourceFile).Length,
                expectedHash,
                CancellationToken.None));
            sourceEntry.MoveTo(destinationAnchor, "book.m4b");
            Assert.True(await sourceEntry.MatchesAsync(
                "verified audio"u8.Length,
                expectedHash,
                CancellationToken.None));
        }

        Assert.False(File.Exists(sourceFile));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(destinationParent, "book.m4b")));
    }

    [Fact]
    public async Task DeleteOpenedFile_RemovesVerifiedPinnedEntry()
    {
        var parent = FileService.GetTempDirectory("pinned-file-delete");
        var file = await FileService.GetFileAsync(parent, "book.m4b", "delete me");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using (var entry = anchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            entry.Delete();
        }

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteOpenedFile_UnixReplacementAtRetirementBoundary_IsPreserved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = FileService.GetTempDirectory("pinned-file-delete-race");
        var file = await FileService.GetFileAsync(parent, "marker.json", "owned");
        var displaced = Path.Join(parent, "marker.original");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var entry = anchor.OpenExistingFile(
            "marker.json",
            requireDeleteAccess: true);
        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(path =>
        {
            if (replaced
                || !Path.GetFileName(path).StartsWith(
                    ".listenarr-retire-",
                    StringComparison.Ordinal))
            {
                return;
            }

            replaced = true;
            File.Move(file, displaced, overwrite: false);
            File.WriteAllText(file, "external");
        });

        Assert.ThrowsAny<Exception>(() => entry.Delete());

        Assert.True(replaced);
        Assert.Equal("owned", await File.ReadAllTextAsync(displaced));
        Assert.Equal("external", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task MoveExistingFileTo_ExistingDestinationPreservesBothFiles()
    {
        var sourceParent = FileService.GetTempDirectory("pinned-file-move-collision-source");
        var destinationParent = FileService.GetTempDirectory("pinned-file-move-collision-destination");
        var sourceFile = await FileService.GetFileAsync(sourceParent, "book.m4b", "source");
        var destinationFile = await FileService.GetFileAsync(destinationParent, "book.m4b", "destination");
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParent);
        using var destinationAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(destinationParent);
        using (var sourceEntry = sourceAnchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            Assert.ThrowsAny<Exception>(() =>
                sourceEntry.MoveTo(destinationAnchor, "book.m4b"));
        }

        Assert.Equal("source", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("destination", await File.ReadAllTextAsync(destinationFile));
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_WindowsNonEmptyHierarchyWithLiveDescendant_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = FileService.GetTempDirectory("pinned-directory-publication-live-hierarchy");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await creation.WriteInsideFileAsync("marker.json", "{}", CancellationToken.None);
        using var rootAnchor = creation.OpenCreatedDirectoryAnchor();
        using var childCreation = rootAnchor.TryCreateChild("child");
        Assert.True(childCreation.Created);
        using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            creation.PublishCreatedDirectoryAs("published").Dispose()));

        Assert.True(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.False(Directory.Exists(Path.Join(parent, "published")));
    }

    [Fact]
    public async Task OpenOrCreateExclusiveLockFileAsync_ContendsAndReleasesAcrossPinnedAnchors()
    {
        var directory = FileService.GetTempDirectory(
            "pinned-exclusive-lock-file");
        using var firstAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
        using var secondAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
        using var firstLock =
            await firstAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock");
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            secondAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock",
                cancellation.Token));

        firstLock.Dispose();
        using var reacquired =
            await secondAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock");
        Assert.True(reacquired.CanRead);
        Assert.True(reacquired.CanWrite);
    }

    [Fact]
    public async Task PublishNewFileAsync_TemporaryNameReplacedBeforeCleanup_PreservesReplacementBytes()
    {
        var parent = FileService.GetTempDirectory("pinned-file-publication-cleanup-race");
        var temporaryName = "marker.json.writing-test";
        var temporaryPath = Path.Join(parent, temporaryName);
        var displacedPath = temporaryPath + ".original";
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);

        await Assert.ThrowsAsync<IOException>(() => anchor.PublishNewFileAsync(
            temporaryName,
            "marker.json",
            () => Task.CompletedTask,
            async stream =>
            {
                await stream.WriteAsync("owned bytes"u8.ToArray());
                stream.Flush(flushToDisk: true);
            },
            () =>
            {
                File.Move(temporaryPath, displacedPath);
                File.WriteAllText(temporaryPath, "external bytes");
                throw new IOException("Simulated publication failure after pathname replacement.");
            },
            _ => false));

        Assert.False(File.Exists(Path.Join(parent, "marker.json")));
        var survivingContents = await Task.WhenAll(
            Directory.EnumerateFiles(parent)
                .Select(path => File.ReadAllTextAsync(path)));
        Assert.Contains("external bytes", survivingContents);
    }

    [Fact]
    public async Task PublishNewFileAsync_UnixTemporaryReplacement_IsDetectedAfterPublication()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = FileService.GetTempDirectory("pinned-file-publication-leaf-race");
        var temporaryName = "marker.json.writing-test";
        var temporaryPath = Path.Join(parent, temporaryName);
        var displacedPath = temporaryPath + ".original";
        var finalPath = Path.Join(parent, "marker.json");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            anchor.PublishNewFileAsync(
                temporaryName,
                "marker.json",
                () => Task.CompletedTask,
                async stream =>
                {
                    await stream.WriteAsync("owned bytes"u8.ToArray());
                    stream.Flush(flushToDisk: true);
                },
                () =>
                {
                    File.Move(temporaryPath, displacedPath);
                    File.WriteAllText(temporaryPath, "external bytes");
                    return Task.CompletedTask;
                },
                _ => true));

        Assert.Equal("owned bytes", await File.ReadAllTextAsync(displacedPath));
        Assert.Equal("external bytes", await File.ReadAllTextAsync(finalPath));
    }
}
