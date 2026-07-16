using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

public sealed class AudiobookFileServiceCoordinationTests
{
    [Fact]
    public async Task ClaimAudiobookFileAsync_AcquiresGlobalBoundaryBeforeAudiobookLock()
    {
        var events = new List<string>();
        var globalCoordinator = new RecordingFilesystemMutationCoordinator(events);
        var audiobookCoordinator = new RecordingAudiobookOperationCoordinator(events);
        var physicalPath = Path.GetFullPath(
            Path.Join(Path.GetTempPath(), $"claim-order-{Guid.NewGuid():N}.m4b"));
        var basePath = Path.GetDirectoryName(physicalPath)!;
        var audiobook = new Audiobook
        {
            Id = 42,
            Title = "Claim Order",
            BasePath = basePath
        };
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetByIdSnapshotAsync(
                audiobook.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobook);
        var fileRepository = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
        fileRepository.Setup(repository => repository.ClaimAsync(
                It.IsAny<AudiobookFile>(),
                It.IsAny<CancellationToken>()))
            .Returns<AudiobookFile, CancellationToken>((file, _) =>
                Task.FromResult(new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.Created,
                    file)));
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(system => system.FileExists(physicalPath)).Returns(true);
        fileSystem.Setup(system => system.IsReparsePoint(physicalPath)).Returns(false);
        var validatedPath = physicalPath;
        var validationReason = string.Empty;
        fileSystem.Setup(system => system.TryValidateMutationTarget(
                physicalPath,
                It.IsAny<IEnumerable<string?>>(),
                out validatedPath,
                out validationReason))
            .Returns(true);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                basePath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                basePath,
                CanonicalPath: basePath));
        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                audiobook,
                physicalPath,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudiobookFilePathIdentity.CreateValid(
                physicalPath,
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                basePath));
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync()).ReturnsAsync([]);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new AudiobookFileService(
            memoryCache,
            new MetadataExtractionLimiter(),
            audiobookRepository.Object,
            fileRepository.Object,
            Mock.Of<IHistoryRepository>(),
            Mock.Of<IMetadataService>(),
            Mock.Of<IToastService>(),
            Mock.Of<IFfmpegService>(),
            fileSystem.Object,
            semanticsResolver.Object,
            identityResolver.Object,
            rootFolderService.Object,
            NullLogger<AudiobookFileService>.Instance,
            globalCoordinator,
            audiobookCoordinator);

        var result = await service.ClaimAudiobookFileAsync(
            audiobook,
            AudiobookFile.CreateUnresolved(physicalPath),
            physicalPath);

        Assert.Equal(AudiobookFileClaimOutcome.Created, result.Outcome);
        Assert.Equal(
            ["global-enter", "audiobook-enter", "audiobook-exit", "global-exit"],
            events);
    }

    private sealed class RecordingFilesystemMutationCoordinator(
        List<string> events) : IFilesystemMutationCoordinator
    {
        public async Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global-enter");
            await operation(cancellationToken);
            events.Add("global-exit");
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global-enter");
            var result = await operation(cancellationToken);
            events.Add("global-exit");
            return result;
        }
    }

    private sealed class RecordingAudiobookOperationCoordinator(
        List<string> events) : IAudiobookOperationCoordinator
    {
        public Task ExecuteExclusiveAsync(
            int audiobookId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            int audiobookId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

        public async Task ExecuteExclusiveAsync(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("audiobook-enter");
            await operation(cancellationToken);
            events.Add("audiobook-exit");
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("audiobook-enter");
            var result = await operation(cancellationToken);
            events.Add("audiobook-exit");
            return result;
        }
    }
}
