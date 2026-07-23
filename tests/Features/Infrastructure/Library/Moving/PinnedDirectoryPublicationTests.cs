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
    public async Task PublishCreatedDirectoryAs_NonEmptyHierarchyWithLiveDescendant_FailsClosed()
    {
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
}
