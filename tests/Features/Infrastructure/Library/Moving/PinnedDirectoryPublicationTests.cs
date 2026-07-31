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

    [WindowsFact]
    public async Task OpenOrRepairOwnedAsync_InterruptedRetentionCopy_RebuildsFromPinnedPublication()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-destination-retention-repair");
        const string publicName = "book.m4b";
        var publicPath = await FileService.GetFileAsync(
            parent,
            publicName,
            "verified audio");
        var operationId = Guid.NewGuid();
        var retentionName = PinnedDestinationRetentionGuard.CreateRetentionName(
            operationId,
            publicName);
        var retentionPath = Path.Join(parent, retentionName);
        await File.WriteAllTextAsync(retentionPath, "partial");
        var expectedBytes = await File.ReadAllBytesAsync(publicPath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes));
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            parent);

        var guard = await PinnedDestinationRetentionGuard
            .OpenOrRepairOwnedAsync(
                anchor,
                publicName,
                retentionName,
                expectedBytes.LongLength,
                expectedHash,
                CancellationToken.None);

        Assert.NotNull(guard);
        using (guard)
        {
            Assert.True(await guard.CurrentPublicationMatchesAsync(
                CancellationToken.None));
            Assert.True(await guard.TryLinearizePublicationAsync(
                CancellationToken.None));
            Assert.True(await guard.CompleteAsync(CancellationToken.None));
        }

        Assert.Equal("verified audio", await File.ReadAllTextAsync(publicPath));
        Assert.False(File.Exists(retentionPath));
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
    public void TryOpenExistingFileWithOutcome_MissingFile_IsNotFound()
    {
        var parent = FileService.GetTempDirectory("pinned-file-open-missing");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);

        var outcome = anchor.TryOpenExistingFileWithOutcome(
            "missing-marker.json",
            requireDeleteAccess: true,
            out var entry);

        Assert.Equal(PinnedFileOpenOutcome.NotFound, outcome);
        Assert.Null(entry);
    }

    [WindowsFact]
    public async Task TryOpenExistingFileWithOutcome_WindowsSharingViolation_IsUnavailable()
    {

        var parent = FileService.GetTempDirectory("pinned-file-open-locked");
        var marker = await FileService.GetFileAsync(
            parent,
            "marker.json",
            "owned marker");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using (File.Open(
            marker,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            var lockedOutcome = anchor.TryOpenExistingFileWithOutcome(
                "marker.json",
                requireDeleteAccess: true,
                out var lockedEntry);

            Assert.Equal(PinnedFileOpenOutcome.Unavailable, lockedOutcome);
            Assert.Null(lockedEntry);
            Assert.True(File.Exists(marker));
        }

        var availableOutcome = anchor.TryOpenExistingFileWithOutcome(
            "marker.json",
            requireDeleteAccess: true,
            out var availableEntry);
        using (availableEntry)
        {
            Assert.Equal(PinnedFileOpenOutcome.Opened, availableOutcome);
            Assert.NotNull(availableEntry);
            Assert.True(availableEntry.VisiblePathMatches());
        }
    }

    [LinuxFact]
    public async Task DeleteOpenedFile_UnixReplacementAtRetirementBoundary_IsPreserved()
    {

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

    [WindowsFact]
    public async Task PublishCreatedDirectoryAs_WindowsNonEmptyHierarchyWithLiveDescendant_FailsClosed()
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
    public async Task ReplaceWithinParent_ExpectedDestinationReplacedBeforeCommit_PreservesEveryGeneration()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-file-conditional-replacement-race");
        var temporaryPath = await FileService.GetFileAsync(
            parent,
            "marker.json.pending",
            "new marker");
        var destinationPath = await FileService.GetFileAsync(
            parent,
            "marker.json",
            "expected predecessor");
        var predecessorPath = Path.Join(parent, "marker.predecessor");
        using (var anchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent))
        using (var temporary = anchor.OpenExistingFile(
            "marker.json.pending",
            requireDeleteAccess: true))
        using (var expected = anchor.OpenExistingFile(
            "marker.json",
            requireDeleteAccess: false))
        {
            Assert.ThrowsAny<Exception>(() => temporary.ReplaceWithinParent(
                "marker.json",
                expected,
                () =>
                {
                    File.Move(destinationPath, predecessorPath);
                    File.WriteAllText(destinationPath, "external replacement");
                }));
        }

        Assert.Equal("new marker", await File.ReadAllTextAsync(temporaryPath));
        Assert.Equal(
            "expected predecessor",
            await File.ReadAllTextAsync(predecessorPath));
        Assert.Equal(
            "external replacement",
            await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task ReplaceWithinParent_PublishedGenerationReplacedBeforePredecessorRetirement_PreservesPredecessor()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-file-post-publication-replacement-race");
        var temporaryPath = await FileService.GetFileAsync(
            parent,
            "marker.json.pending",
            "new marker");
        var destinationPath = await FileService.GetFileAsync(
            parent,
            "marker.json",
            "expected predecessor");
        var displacedPublishedPath = Path.Join(parent, "published-generation.marker");
        var replacementSucceeded = false;
        Exception? failure;
        using (var anchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent))
        using (var temporary = anchor.OpenExistingFile(
            "marker.json.pending",
            requireDeleteAccess: true))
        using (var expected = anchor.OpenExistingFile(
            "marker.json",
            requireDeleteAccess: false))
        {
            failure = Record.Exception(() => temporary.ReplaceWithinParent(
                "marker.json",
                expected,
                afterPublication: () =>
                {
                    try
                    {
                        File.Move(destinationPath, displacedPublishedPath);
                        File.WriteAllText(destinationPath, "external replacement");
                        replacementSucceeded = true;
                    }
                    catch (IOException)
                    {
                        // Windows pins the published generation without delete sharing.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Some Windows filesystems report sharing denial as access denied.
                    }
                }));
        }

        if (!replacementSucceeded)
        {
            Assert.Null(failure);
            Assert.Equal("new marker", await File.ReadAllTextAsync(destinationPath));
            Assert.False(File.Exists(temporaryPath));
            Assert.Empty(Directory.EnumerateFiles(
                parent,
                "*.listenarr-predecessor.tmp",
                SearchOption.TopDirectoryOnly));
            return;
        }

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("external replacement", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("new marker", await File.ReadAllTextAsync(displacedPublishedPath));
        Assert.Equal("expected predecessor", await File.ReadAllTextAsync(temporaryPath));
    }

    [Fact]
    public async Task ReplaceWithinParent_ExpectedDestinationUnchanged_PublishesAndRetiresPredecessor()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-file-conditional-replacement-success");
        await FileService.GetFileAsync(
            parent,
            "marker.json.pending",
            "new marker");
        await FileService.GetFileAsync(
            parent,
            "marker.json",
            "expected predecessor");
        using (var anchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent))
        using (var temporary = anchor.OpenExistingFile(
            "marker.json.pending",
            requireDeleteAccess: true))
        using (var expected = anchor.OpenExistingFile(
            "marker.json",
            requireDeleteAccess: false))
        {
            temporary.ReplaceWithinParent("marker.json", expected);
        }

        Assert.Equal(
            "new marker",
            await File.ReadAllTextAsync(Path.Join(parent, "marker.json")));
        Assert.False(File.Exists(Path.Join(parent, "marker.json.pending")));
        Assert.Empty(Directory.EnumerateFiles(
            parent,
            "*.listenarr-predecessor.tmp",
            SearchOption.TopDirectoryOnly));
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

    [LinuxFact]
    public async Task PublishNewFileAsync_UnixTemporaryReplacement_IsDetectedAfterPublication()
    {

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
