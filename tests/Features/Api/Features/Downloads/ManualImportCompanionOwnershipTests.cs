using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportCompanionOwnershipTests")]
[Trait("Category", "Integration")]
public sealed class ManualImportCompanionOwnershipTests : BaseTests
{
    [Fact]
    public async Task ImportAsync_DestinationOwnedByAnotherAudiobook_DoesNotWriteCompanion()
    {
        Init();
        var testRoot = FileService.GetTempDirectory(
            $"manual-import-companion-owned-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "target-book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var selectedSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "bonus.m4b");
        var selectedDestination = Path.Join(destinationDirectory, "book.m4b");
        var companionDestination = Path.Join(destinationDirectory, "bonus.m4b");
        await File.WriteAllTextAsync(selectedSource, "selected audio");
        await File.WriteAllTextAsync(companionSource, "companion audio");
        await File.WriteAllTextAsync(companionDestination, "previous audio");

        var targetAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Target Book")
                .WithAuthor("Target Author")
                .WithBasePath(destinationDirectory)
                .Build());
        var otherAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Other Book")
                .WithAuthor("Other Author")
                .WithBasePath(destinationDirectory)
                .Build());
        var fileService = _provider.GetRequiredService<IAudiobookFileService>();
        Assert.True(await fileService.EnsureAudiobookFileAsync(
            otherAudiobook,
            companionDestination,
            "test"));
        File.Delete(companionDestination);

        var metadata = new AudioMetadata
        {
            Title = "Target Book",
            Artist = "Target Author",
            Duration = TimeSpan.FromMinutes(10),
            Format = "m4b"
        };
        var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
        metadataService
            .Setup(service => service.ExtractFileMetadataAsync(companionSource))
            .ReturnsAsync(metadata);
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
            .ReturnsAsync([]);
        var semanticsResolver = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
        var importer = new ManualImportCompanionImporter(
            metadataService.Object,
            mover.Object,
            new LocalFileSystem(),
            semanticsResolver,
            ownershipStore.Object,
            NullLogger<ManualImportCompanionImporter>.Instance,
            fileService);
        var tracker = new ManualImportDestinationTracker(
            new LocalFileSystem(),
            semanticsResolver);
        var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
        var selectedProfiles = new[]
        {
            FileUtils.CreateAudioMatchProfile(selectedSource, metadata)
        };
        var items = new[]
        {
            new ManualImportItemDto
            {
                FullPath = selectedSource,
                MatchedAudiobookId = targetAudiobook.Id
            }
        };
        var results = new[]
        {
            new ManualImportResultDto
            {
                Success = true,
                SourcePath = selectedSource,
                DestinationPath = selectedDestination,
                Audiobook = targetAudiobook
            }
        };

        var imported = await importer.ImportAsync(
            FileAction.Copy,
            items,
            results,
            sourceDirectory,
            selectedProfiles,
            tracker,
            sourceResolution.Semantics,
            importBlacklist: []);

        Assert.Equal(0, imported);
        Assert.True(File.Exists(companionSource));
        Assert.False(File.Exists(companionDestination));
        mover.Verify(
            service => service.PerformActionOn(
                It.IsAny<FileAction>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }
}
