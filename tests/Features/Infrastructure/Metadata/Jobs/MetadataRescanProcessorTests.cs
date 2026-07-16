/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs
{
    [Trait("Area", "Metadata")]
    [Trait("Name", "MetadataRescanProcessorTests")]
    public sealed class MetadataRescanProcessorTests : BaseTests
    {
        [Fact]
        public async Task RunCycleAsync_PathChangesDuringExtraction_DiscardsStaleMetadataResult()
        {
            var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<string>()))
                .Returns(async () =>
                {
                    extractionStarted.SetResult();
                    await releaseExtraction.Task;
                    return new AudioMetadata
                    {
                        Duration = TimeSpan.FromSeconds(321),
                        Format = "m4b"
                    };
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var oldPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-stale-old"),
                "book.m4b",
                "old audio");
            var newPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-stale-new"),
                "book.m4b",
                "new audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stale Metadata Extraction")
                .WithBasePath(Path.GetDirectoryName(oldPath)!)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(oldPath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            var cycle = processor.RunCycleAsync(CancellationToken.None);
            await extractionStarted.Task;

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var moveContext = await factory.CreateDbContextAsync())
            {
                var movedFile = await moveContext.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
                movedFile.Path = newPath;
                await moveContext.SaveChangesAsync();
            }

            releaseExtraction.SetResult();
            await cycle;

            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
            Assert.Equal(newPath, persisted.Path);
            Assert.Null(persisted.DurationSeconds);
            Assert.Null(persisted.Format);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivityMode.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivityMode.Insensitive, true)]
        public async Task RunCycleAsync_NonAudioFile_ClearsLegacyFilePathUsingResolvedRootSemantics(
            FileSystemCaseSensitivityMode caseSensitivityMode,
            bool shouldClearLegacyPath)
        {
            var rootPath = FileService.GetTempDirectory("metadata-rescan-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Metadata Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(caseSensitivityMode)
                .WithIsDefault()
                .Build());
            var audiobookPath = Path.Join(rootPath, "CaseBook", "book.txt");
            var filePath = Path.Join(rootPath, "casebook", "book.txt");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Case Book")
                .WithFilePath(audiobookPath)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(filePath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            using var verificationScope = _provider.CreateScope();
            var verificationAudiobooks = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var verificationFiles = verificationScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var updated = await verificationAudiobooks.GetByIdAsync(audiobook.Id);
            var removed = await verificationFiles.GetByIdAsync(file.Id);
            Assert.Null(removed);
            if (shouldClearLegacyPath)
            {
                Assert.Null(updated?.FilePath);
                Assert.Null(updated?.FileSize);
            }
            else
            {
                Assert.Equal(audiobookPath, updated?.FilePath);
            }
        }

        [Fact]
        public async Task RunCycleAsync_NestedRootUsesMostSpecificSemanticsForLegacyPathCleanup()
        {
            var outerRoot = FileService.GetTempDirectory("metadata-rescan-nested-outer");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            Directory.CreateDirectory(innerRoot);
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A Outer")
                .WithPath(outerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Z Inner")
                .WithPath(innerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());
            var audiobookPath = Path.Join(innerRoot, "CaseBook", "book.txt");
            var filePath = Path.Join(innerRoot, "casebook", "book.txt");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested Case Book")
                .WithFilePath(audiobookPath)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(filePath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            using var verificationScope = _provider.CreateScope();
            var verificationAudiobooks = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var verificationFiles = verificationScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var updated = await verificationAudiobooks.GetByIdAsync(audiobook.Id);
            var removed = await verificationFiles.GetByIdAsync(file.Id);
            Assert.Null(removed);
            Assert.Equal(audiobookPath, updated?.FilePath);
        }
    }
}
