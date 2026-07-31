using Listenarr.Tests.Common;
using Xunit.Sdk;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        if (IsRequired() || CanCreateDirectoryLink(out _))
        {
            return;
        }

        Skip = "Directory symbolic links are unavailable on this test runner.";
    }

    private static bool IsRequired() =>
        string.Equals(
            Environment.GetEnvironmentVariable("LISTENARR_REQUIRE_DIRECTORY_LINK_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool CanCreateDirectoryLink(out string? reason)
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
            reason = null;
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0
                && Directory.ResolveLinkTarget(link, returnFinalTarget: true) != null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(link)
                    && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(link);
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target);
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                // Best effort discovery-time cleanup.
            }
        }
    }
}

[Trait("Area", "Library")]
[Trait("Name", "DirectoryCreationParentReplacementTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryCreationParentReplacementTests : BaseTests
{
    [DirectoryLinkFact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedBeforeHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: true);

    [DirectoryLinkFact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: false);

    [DirectoryLinkFact]
    public async Task EnsureCreatedHierarchyAsync_LinkedManagedBoundary_CreatesInsidePinnedTarget()
    {
        var root = FileService.GetTempDirectory("directory-create-linked-boundary");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability(root);
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        await AddAuthorizedRootAsync(linkedBoundary, "Linked Boundary Test Root");

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

    [DirectoryLinkFact]
    public async Task EnsureCreatedHierarchyAsync_LinkedManagedBoundary_TopLevelOwnershipResolves()
    {
        var root = FileService.GetTempDirectory("directory-create-linked-top-level");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability(root);
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        await AddAuthorizedRootAsync(linkedBoundary, "Linked Top-Level Test Root");

        var destination = Path.Join(linkedBoundary, "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        try
        {
            var created = await store.EnsureCreatedHierarchyAsync(
                destination,
                linkedBoundary,
                semantics,
                "linked-top-level-regression");

            Assert.Single(created);
            Assert.True(Directory.Exists(Path.Join(physicalBoundary, "Book")));
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

    [DirectoryLinkFact]
    public void PinnedFileEntry_DeleteUnderLinkedBoundary_RetiresThroughPinnedTarget()
    {
        var root = FileService.GetTempDirectory("pinned-file-retirement-linked-boundary");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability(root);
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        var fileName = "retire.tmp";

        try
        {
            using var boundary =
                PinnedDirectoryCreation.OpenPinnedBoundary(linkedBoundary);
            using var file = boundary.CreateNewFile(fileName, hiddenFile: true);
            using (var stream = file.OpenWriteStream(
                bufferSize: 4096,
                asynchronous: false))
            {
                stream.WriteByte(1);
                stream.Flush(flushToDisk: true);
            }

            file.Delete();

            Assert.False(File.Exists(Path.Join(physicalBoundary, fileName)));
            Assert.Empty(Directory.EnumerateDirectories(
                physicalBoundary,
                ".listenarr-retire-*.state",
                SearchOption.TopDirectoryOnly));
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
        Directory.CreateDirectory(parent);
        RequireDirectoryLinkCapability(root);
        await AddAuthorizedRootAsync(root, "Parent Replacement Test Root");

        var destination = Path.Join(parent, "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        var hookRan = false;
        Exception? hookFailure = null;
        void ReplaceParent(string path)
        {
            var expectedPath = replaceBeforeOpen ? parent : destination;
            if (hookRan || !string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            try
            {
                Directory.Move(parent, displacedParent);
                Directory.CreateSymbolicLink(parent, external);
            }
            catch (Exception exception)
            {
                hookFailure = exception;
                throw;
            }
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
            Assert.Null(hookFailure);
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

    private static void RequireDirectoryLinkCapability(string root)
    {
        var targetPath = Path.Join(root, $"link-capability-target-{Guid.NewGuid():N}");
        var linkPath = Path.Join(root, $"link-capability-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetPath);
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            var attributes = File.GetAttributes(linkPath);
            Assert.True(
                (attributes & FileAttributes.ReparsePoint) != 0,
                "The directory-link capability probe did not create a reparse point.");
            Assert.NotNull(Directory.ResolveLinkTarget(linkPath, returnFinalTarget: true));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            var reason =
                $"Directory symbolic links are unavailable on this test runner: {exception.Message}";
            if (string.Equals(
                Environment.GetEnvironmentVariable("LISTENARR_REQUIRE_DIRECTORY_LINK_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new XunitException(reason);
            }

            throw new XunitException(
                $"{reason} The capability changed after test discovery.");
        }
        finally
        {
            TryDeleteDirectoryLink(linkPath);
            try
            {
                Directory.Delete(targetPath);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                // Best effort test cleanup. The per-test temporary root is removed by BaseTests.
            }
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
