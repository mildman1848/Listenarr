namespace Listenarr.Tests.Features.Application.Audiobooks.Moving;

public sealed class AudiobookDestinationRewriteServiceTests
{
    [Fact]
    public async Task RewriteDestinationAsync_RepairsLegacyInvalidBasePathWhenExpectedSourceMatchesExactly()
    {
        var rootPath = Path.Join(Path.GetTempPath(), $"listenarr-root-{Guid.NewGuid():N}");
        var destinationPath = Path.Join(rootPath, "Author", "Valid Title");
        const string legacyInvalidSourcePath = "\0invalid";
        var normalizedLegacySourcePath = FileUtils.NormalizeStoredPath(legacyInvalidSourcePath);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var rootSemantics = FileSystemPathSemantics.CurrentHostDefault;

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    IsDefault = true,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                }
            ]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(rootSemantics, PathIdentityState.Valid, rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(85))
            .ReturnsAsync(new Audiobook
            {
                Id = 85,
                Title = "Legacy Title",
                BasePath = legacyInvalidSourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                85,
                normalizedLegacySourcePath,
                destinationPath,
                rootSemantics,
                rootSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var result = await service.RewriteDestinationAsync(
            85,
            destinationPath,
            expectedSourcePath: legacyInvalidSourcePath);

        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.Equal(normalizedLegacySourcePath, result.SourcePath);
        fileSystem.Verify(service => service.DirectoryExists(It.IsAny<string>()), Times.Never);
        semanticsResolver.Verify(service => service.ResolveAsync(
            rootPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RewriteDestinationAsync_UpdatesMetadataWithoutSourceFilesystemAccess()
    {
        var rootPath = Path.Join(Path.GetTempPath(), $"listenarr-root-{Guid.NewGuid():N}");
        var sourcePath = Path.Join(rootPath, "Author", "Old Title");
        var destinationPath = Path.Join(rootPath, "Author", "Missing Title");
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var rootSemantics = FileSystemPathSemantics.CurrentHostDefault;

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    IsDefault = true,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                }
            ]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(rootSemantics, PathIdentityState.Valid, rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(85))
            .ReturnsAsync(new Audiobook
            {
                Id = 85,
                Title = "Old Title",
                BasePath = sourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                85,
                sourcePath,
                destinationPath,
                rootSemantics,
                rootSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var result = await service.RewriteDestinationAsync(85, destinationPath, expectedSourcePath: null);

        Assert.Equal(85, result.AudiobookId);
        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.Equal(sourcePath, result.SourcePath);
        fileSystem.Verify(service => service.DirectoryExists(It.IsAny<string>()), Times.Never);
        semanticsResolver.Verify(service => service.ResolveAsync(
            rootPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
