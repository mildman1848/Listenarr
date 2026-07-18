using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportCompanionImporterTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportCompanionImporterTests : BaseTests
{
    [Fact]
    public async Task ImportAsync_CanceledAfterOwnershipPreparation_DoesNotMutateCompanionFile()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-canceled-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var audioSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "cover.jpg");
        var audioDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(audioSource, "audio");
        await File.WriteAllTextAsync(companionSource, "image");

        try
        {
            using var cancellation = new CancellationTokenSource();
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>(MockBehavior.Strict);
            ownershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => cancellation.Cancel())
                .ReturnsAsync([]);
            var semanticsResolver = new FileSystemSemanticsResolver();
            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                new LocalFileSystem(),
                semanticsResolver,
                ownershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                semanticsResolver);
            var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = audioSource,
                    MatchedAudiobookId = 42
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = audioSource,
                    DestinationPath = audioDestination
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(
                FileAction.Copy,
                items,
                results,
                sourceDirectory,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                importBlacklist: [],
                cancellationToken: cancellation.Token));

            Assert.True(File.Exists(companionSource));
            Assert.False(File.Exists(Path.Join(destinationDirectory, "cover.jpg")));
            mover.Verify(
                service => service.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
            ownershipStore.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportAsync_SelectedSourceOutsideRequestedRoot_MapsCompanionBesideSuccessfulAudioDestination()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-{Guid.NewGuid():N}");
        var requestedRoot = Path.Join(testRoot, "requested-root");
        var selectedDirectory = Path.Join(testRoot, "selected-on-another-boundary");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(requestedRoot);
        Directory.CreateDirectory(selectedDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var audioSource = Path.Join(selectedDirectory, "book.m4b");
        var companionSource = Path.Join(selectedDirectory, "cover.jpg");
        var audioDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(audioSource, "audio");
        await File.WriteAllTextAsync(companionSource, "image");

        try
        {
            string? capturedDestination = null;
            var mover = new Mock<IFileMover>();
            mover.Setup(service => service.PerformActionOn(
                    FileAction.Copy,
                    companionSource,
                    It.IsAny<string>()))
                .Callback<FileAction, string, string?>((_, _, destination) =>
                    capturedDestination = destination)
                .ReturnsAsync(true);
            var semanticsResolver = new FileSystemSemanticsResolver();
            var directoryOwnershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
            directoryOwnershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                new LocalFileSystem(),
                semanticsResolver,
                directoryOwnershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                semanticsResolver);
            var sourceResolution = await semanticsResolver.ResolveAsync(requestedRoot);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = audioSource,
                    MatchedAudiobookId = 42
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = audioSource,
                    DestinationPath = audioDestination
                }
            };

            var imported = await importer.ImportAsync(
                FileAction.Copy,
                items,
                results,
                requestedRoot,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                importBlacklist: []);

            Assert.Equal(1, imported);
            Assert.Equal(
                Path.Join(destinationDirectory, "cover.jpg"),
                capturedDestination);
            Assert.True(FileSystemPathIdentity.IsSameOrInside(
                capturedDestination!,
                destinationDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
