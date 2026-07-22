using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "DirectoryCreationParentReplacementTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryCreationParentReplacementTests : BaseTests
{
    [Fact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedBeforeHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: true);

    [Fact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: false);

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_LinkedManagedBoundary_CreatesInsidePinnedTarget()
    {
        var root = FileService.GetTempDirectory("directory-create-linked-boundary");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        if (!TryCreateDirectoryLink(linkedBoundary, physicalBoundary))
        {
            return;
        }

        var destination = Path.Join(linkedBoundary, "Author", "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        try
        {
            var created = await store.EnsureCreatedHierarchyAsync(
                destination,
                linkedBoundary,
                semantics,
                "linked-boundary-regression");

            Assert.Equal(2, created.Count);
            Assert.True(Directory.Exists(Path.Join(physicalBoundary, "Author", "Book")));
            Assert.True(File.Exists(Path.Join(
                physicalBoundary,
                "Author",
                "Book",
                ".listenarr-directory-owner.json")));
            var resolution = await store.ResolveOwnedAsync(
                destination,
                semantics,
                CancellationToken.None);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Owned,
                resolution.State);
        }
        finally
        {
            TryDeleteDirectoryLink(linkedBoundary);
        }
    }

    private async Task AssertParentReplacementBlockedAsync(bool replaceBeforeOpen)
    {
        var suffix = replaceBeforeOpen ? "before-open" : "after-open";
        var root = FileService.GetTempDirectory($"directory-create-parent-race-root-{suffix}");
        var parent = Path.Join(root, "Author");
        var displacedParent = Path.Join(root, "Author.original");
        var external = FileService.GetTempDirectory($"directory-create-parent-race-external-{suffix}");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(parent);

        if (!TryCreateDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var destination = Path.Join(parent, "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        var hookRan = false;
        void ReplaceParent(string path)
        {
            var expectedPath = replaceBeforeOpen ? parent : destination;
            if (hookRan || !string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(parent, displacedParent);
            Directory.CreateSymbolicLink(parent, external);
        }

        using var hook = replaceBeforeOpen
            ? ExclusiveDirectoryCreator.PushBeforeOpenParentHook(ReplaceParent)
            : ExclusiveDirectoryCreator.PushBeforeCreateHook(ReplaceParent);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.EnsureCreatedHierarchyAsync(
                    destination,
                    root,
                    semantics,
                    "parent-replacement-regression"));

            Assert.True(hookRan);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.False(File.Exists(Path.Join(
                external,
                ".listenarr-directory-owner.json")));
            Assert.False(Directory.Exists(Path.Join(external, "Book")));
            var resolution = await store.ResolveOwnedAsync(
                destination,
                semantics,
                CancellationToken.None);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
        }
        finally
        {
            TryDeleteDirectoryLink(parent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(parent))
            {
                Directory.Move(displacedParent, parent);
            }
        }
    }

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
            // Best effort test cleanup. The per-test temporary root is removed by BaseTests.
        }
    }
}
