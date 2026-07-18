using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "AudiobookScanServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookScanServiceTests : BaseTests
{
    [Fact]
    public async Task ScanAsync_SameAuthorSiblingBooks_ClaimsOnlyRequestedBook()
    {
        var root = FileService.GetTempDirectory("shared-scan-service");
        var requestedDirectory = Path.Join(root, "Shared Author", "Book One");
        var siblingDirectory = Path.Join(root, "Shared Author", "Book Two");
        Directory.CreateDirectory(requestedDirectory);
        Directory.CreateDirectory(siblingDirectory);
        var requestedFile = await FileService.GetFileAsync(
            requestedDirectory,
            "Book One.m4b",
            "audio");
        _ = await FileService.GetFileAsync(
            siblingDirectory,
            "Book Two.m4b",
            "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Book One")
            .WithAuthor("Shared Author")
            .Build());

        var result = await ScanAsync(audiobook, root);

        var tracked = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(requestedFile, tracked.Path);
        Assert.Equal(requestedDirectory, result.Audiobook.BasePath);
        Assert.DoesNotContain(
            result.AttributedFiles,
            path => FileSystemPathIdentity.IsSameOrInside(
                path,
                siblingDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
    }

    [Fact]
    public async Task ScanAsync_CompleteScan_RemovesOnlyVerifiedMissingRow()
    {
        var root = FileService.GetTempDirectory("scan-service-missing");
        var missingPath = Path.Join(root, "Missing Book.m4b");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(audiobook, root);

        Assert.True(result.IsComplete);
        Assert.True(result.ReconciliationPerformed);
        var removed = Assert.Single(result.RemovedFiles);
        Assert.Equal(tracked.Id, removed.Id);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
        Assert.Contains(history, entry =>
            entry.EventType == "File Removed"
            && entry.Message != null
            && entry.Message.Contains("Verified missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_FocusedScope_PreservesMissingRowOutsideScope()
    {
        var root = FileService.GetTempDirectory("scan-service-focused-root");
        var focused = Path.Join(root, "Book", "CD1");
        var outsideMissing = Path.Join(root, "Book", "CD2", "02.mp3");
        Directory.CreateDirectory(focused);
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Book")
            .WithBasePath(Path.Join(root, "Book"))
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(outsideMissing)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(
            audiobook,
            focused,
            isAuthoritativeScope: false);

        Assert.False(result.ReconciliationPerformed);
        Assert.Empty(result.RemovedFiles);
        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
    }

    [Fact]
    public async Task ScanAsync_ExistingUnattributedLegacyPath_IsNotClaimed()
    {
        var root = FileService.GetTempDirectory("scan-service-legacy");
        var foreignPath = await FileService.GetFileAsync(
            root,
            "Another Book.m4b",
            "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Requested Book")
            .WithBasePath(root)
            .WithFilePath(foreignPath)
            .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(foreignPath, result.Audiobook.FilePath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "LegacyPathNotAttributed");
    }

    [Fact]
    public async Task ScanAsync_RootDisappearsAfterDiscovery_PreservesTrackedRows()
    {
        var root = FileService.GetTempDirectory("scan-service-root-disappears");
        var missingPath = Path.Join(root, "Missing Book.m4b");
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.SetupSequence(system => system.DirectoryExists(root))
            .Returns(true)
            .Returns(false);
        fileSystem.Setup(system => system.IsReparsePoint(root)).Returns(false);
        fileSystem.Setup(system => system.EnumerateFiles(root)).Returns([]);
        fileSystem.Setup(system => system.EnumerateDirectories(root)).Returns([]);
        _services.AddSingleton(fileSystem.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            ScanAsync(audiobook, root));

        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
    }

    [Fact]
    public async Task ScanAsync_ConfiguredAuthorityChangesDuringDiscovery_DoesNotMutate()
    {
        var root = FileService.GetTempDirectory("scan-service-authority-race");
        _ = await FileService.GetFileAsync(root, "Race Book.m4b", "audio");
        var identity = PathIdentitySnapshot.FromResolution(
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            FileService.GetTempPath(),
            root);
        var authorization = new Mock<IScanPathAuthorizationService>(
            MockBehavior.Strict);
        authorization.SetupSequence(service => service.AuthorizeAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScanPathAuthorizationResult.Authorized(root, identity))
            .ReturnsAsync(ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.OutsideConfiguredRoots,
                "root changed"));
        _services.AddSingleton(authorization.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Race Book")
                .Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetRequiredService<IAudiobookScanService>()
                .ScanAsync(new AudiobookScanCommand(
                    audiobook.Id,
                    root,
                    identity)));

        Assert.Contains("root changed", exception.Message);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        var persisted = await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id);
        Assert.Null(persisted!.BasePath);
    }

    [Fact]
    public async Task ScanAsync_IncompleteEnumeration_PreservesMissingTrackedRows()
    {
        var root = FileService.GetTempDirectory("scan-service-incomplete");
        var failingDirectory = Path.Join(root, "Book");
        Directory.CreateDirectory(failingDirectory);
        var missingPath = Path.Join(failingDirectory, "Missing Book.m4b");
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(system => system.DirectoryExists(root)).Returns(true);
        fileSystem.Setup(system => system.IsReparsePoint(It.IsAny<string>())).Returns(false);
        fileSystem.Setup(system => system.EnumerateFiles(root)).Returns([]);
        fileSystem.Setup(system => system.EnumerateDirectories(root))
            .Returns([failingDirectory]);
        fileSystem.Setup(system => system.EnumerateFiles(failingDirectory))
            .Throws(new UnauthorizedAccessException("denied"));
        _services.AddSingleton(fileSystem.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(audiobook, root);

        Assert.False(result.IsComplete);
        Assert.False(result.ReconciliationPerformed);
        Assert.Empty(result.RemovedFiles);
        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ReconciliationSkippedIncompleteScan");
    }

    private async Task<AudiobookScanResult> ScanAsync(
        Audiobook audiobook,
        string scanRoot,
        bool isAuthoritativeScope = true)
    {
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());
        var resolution = await _provider
            .GetRequiredService<IFileSystemSemanticsResolver>()
            .ResolveAsync(scanRoot);
        Assert.Equal(PathIdentityState.Valid, resolution.State);
        var identity = PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            FileService.GetTempPath(),
            scanRoot);
        return await _provider.GetRequiredService<IAudiobookScanService>()
            .ScanAsync(new AudiobookScanCommand(
                audiobook.Id,
                scanRoot,
                identity,
                IsAuthoritativeScope: isAuthoritativeScope));
    }
}
