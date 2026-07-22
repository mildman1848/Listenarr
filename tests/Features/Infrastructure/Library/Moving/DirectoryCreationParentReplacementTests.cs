using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "DirectoryCreationParentReplacementTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryCreationParentReplacementTests : BaseTests
{
    [Fact]
    public async Task EnsureCreatedHierarchyAsync_ParentReplacedAfterValidation_DoesNotCreateOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("directory-create-parent-race-root");
        var parent = Path.Join(root, "Author");
        var displacedParent = Path.Join(root, "Author.original");
        var external = FileService.GetTempDirectory("directory-create-parent-race-external");
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
        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(path =>
        {
            if (hookRan || !string.Equals(path, destination, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(parent, displacedParent);
            Directory.CreateSymbolicLink(parent, external);
        });

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
