/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_MoveTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_MoveTests : BaseTests
    {
        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ReturnsBadRequest_WhenSourceDoesNotExist")]
        public async Task MoveAudiobook_ReturnsBadRequest_WhenSourceDoesNotExist()
        {
            // Given
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(Path.Join(FileService.GetTempPath(), "nonexistent"))
                .Build());

            var request = new LibraryController.MoveRequest { DestinationPath = Path.Join(outputPath, "target") };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            // Then: expect 400 Bad Request with 'Source path' message
            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("Source path", badObj.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "EnqueuesJob_WhenSourceExists")]
        public async Task MoveAudiobook_EnqueuesJob_WhenSourceExists()
        {
            // Given
            var mockMoveQueue = new Mock<IMoveQueueService>();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var target = Path.Join(outputPath, "listenarr-move-dst");
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = target,
                DeleteEmptySource = false
            };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            // Then: expect 202 Accepted
            var acceptedObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, acceptedObj.StatusCode);
            Assert.NotNull(acceptedObj.Value);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == ab.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(target)
                    && command.SourcePath == ab.BasePath
                    && !command.DeleteEmptySource
                    && command.SourceCleanupBoundary == null),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "CreatesCustomPhysicalDestinationOutsideConfiguredRoots")]
        public async Task MoveAudiobook_MoveFilesTrue_AllowsCustomDestinationOutsideConfiguredRootsWithoutCreatingParent()
        {
            var mockMoveQueue = new Mock<IMoveQueueService>();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var configuredOutputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredOutputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Custom Physical Move")
                .WithBasePath(sourcePath)
                .Build());
            var customRoot = FileService.GetTempDirectory("listenarr-custom-destination");
            var target = Path.Join(customRoot, "Author", "Title", "test");
            var targetParent = Path.GetDirectoryName(target)!;

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = target,
                    SourcePath = sourcePath,
                    MoveFiles = true,
                    DeleteEmptySource = true
                });

            var accepted = Assert.IsType<AcceptedResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            Assert.False(Directory.Exists(targetParent));
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(target)
                    && command.SourcePath == sourcePath
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == Path.GetDirectoryName(sourcePath)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_CustomSiblingMove_PersistsCommonSeriesCleanupBoundary()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));
            var configuredOutputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredOutputPath)
                .Build());

            var customRoot = FileService.GetTempDirectory("listenarr-custom-sibling-root");
            var series = Path.Join(customRoot, "Matt Dinniman", "Dungeon Crawler Carl");
            var source = Path.Join(series, "A Parade of Horribles (20262)", "test");
            Directory.CreateDirectory(source);
            var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Parade of Horribles")
                .WithBasePath(source)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    SourcePath = source,
                    DestinationPath = target,
                    MoveFiles = true,
                    DeleteEmptySource = true
                });

            Assert.IsType<AcceptedResult>(result);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(target)
                    && command.SourcePath == source
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == series),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_RelocationConflictReturnsConflict()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MoveRelocationConflictException(
                    "Move target overlaps an active root folder relocation boundary."));
            Init(services => services.WithSingleton(moveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = Path.Join(outputPath, "listenarr-move-dst")
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("active root folder relocation", conflict.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UpdatesBasePath_WhenMoveFilesFalse")]
        public async Task MoveAudiobook_UpdatesBasePath_WhenMoveFilesFalse()
        {
            // Given
            var mockMoveQueue = new Mock<IMoveQueueService>();

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(Path.Join(FileService.GetTempPath(), "listenarr-move-src"))
                .Build());

            var target = Path.Join(outputPath, "listenarr-move-dst");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            // When
            var result = await controller.EnqueueMove(ab.Id, request);

            // Then: expect 200 OK
            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);
            Assert.NotNull(okObj.Value);

            // Ensure DB was updated
            var updated = await _audiobookRepository.GetByIdAsync(ab.Id);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated!.BasePath);

            // Ensure move queue was NOT enqueued
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Theory]
        [InlineData("source")]
        [InlineData("target")]
        public async Task MoveAudiobook_MetadataOnlyRejectsActiveRelocationBoundary(string protectedEndpoint)
        {
            var moveQueue = new Mock<IMoveQueueService>();
            var relocation = new Mock<IRootFolderRelocationService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton(relocation.Object));

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(outputPath, "listenarr-move-dst");
            var protectedPath = protectedEndpoint == "source"
                ? sourcePath
                : FileUtils.NormalizeStoredPath(targetPath);
            relocation.Setup(service => service.IsBoundaryProtectedAsync(
                    protectedPath,
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath, MoveFiles = false });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("active root folder relocation", conflict.Value?.ToString() ?? string.Empty);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsStaleMetadataOnlySourcePath")]
        public async Task MoveAudiobook_MetadataOnlyRejectsStaleSourcePath()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services.WithSingleton(moveQueue.Object));

            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutOutputPath()
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var sourcePath = Path.Join(rootPath, "Author", "Title");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stale Source")
                .WithBasePath(sourcePath)
                .Build());
            var staleSourcePath = Path.Join(rootPath, "Author", "OldTitle");
            var targetPath = Path.Join(rootPath, "Author", "NewTitle");

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = false,
                    SourcePath = staleSourcePath
                });

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("source path changed", conflict.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "ResolvesDestinationAfterAcquiringMutationCoordinator")]
        public async Task MoveAudiobook_MetadataOnlyResolvesDestinationInsideMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));

            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutOutputPath()
                .Build());
            var root = new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build();
            await _rootFolderRepository.AddAsync(root);

            var sourcePath = Path.Join(rootPath, "Author", "Title");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Coordinator")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(rootPath, "Author", "UpdatedTitle");

            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest
                {
                    DestinationPath = targetPath,
                    MoveFiles = false
                });
            await Task.Delay(50);
            Assert.False(moveTask.IsCompleted);

            await _rootFolderRepository.RemoveAsync(root.Id);

            releaseLock.SetResult();
            await lockTask;
            var result = await moveTask;
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("configured root folder or output path", badRequest.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourcePath, (await _audiobookRepository.GetByIdAsync(audiobook.Id))!.BasePath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task MoveAudiobook_MetadataOnlyWaitsForFilesystemMutationCoordinator()
        {
            var coordinator = new FilesystemMutationCoordinator();
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services
                .WithSingleton(moveQueue.Object)
                .WithSingleton<IFilesystemMutationCoordinator>(coordinator));
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(outputPath, "metadata-target");
            var lockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lockTask = coordinator.ExecuteExclusiveAsync(async _ =>
            {
                lockEntered.SetResult();
                await releaseLock.Task;
            });
            await lockEntered.Task;

            var moveTask = _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath, MoveFiles = false });
            await Task.Delay(50);
            Assert.False(moveTask.IsCompleted);

            releaseLock.SetResult();
            await lockTask;
            Assert.IsType<OkObjectResult>(await moveTask);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "PreservesDestinationPathWhitespace_WhenMoveFilesFalse")]
        public async Task MoveAudiobook_PreservesDestinationPathWhitespace_WhenMoveFilesFalse()
        {
            // Given
            var outputPath = FileService.GetTempPath();
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            var relativeTarget = "  listenarr-move-dst-" + Guid.NewGuid().ToString("N");
            var request = new LibraryController.MoveRequest { DestinationPath = relativeTarget, MoveFiles = false };

            // When
            var result = await controller.EnqueueMove(audiobook.Id, request);

            // Then
            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(Path.Join(outputPath, relativeTarget)), updated.BasePath);
            Assert.StartsWith("  listenarr-move-dst-", Path.GetFileName(updated.BasePath), StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsInvalidDestinationPath")]
        public async Task MoveAudiobook_RejectsInvalidDestinationPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join(FileService.GetTempPath(), "bad\0target"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("DestinationPath", badObj.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsAbsoluteDestinationInsideConfiguredRootFolder")]
        public async Task MoveAudiobook_AllowsAbsoluteDestinationInsideConfiguredRootFolder()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var target = Path.Join(rootPath, "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RewritesStoredPathsWithoutMovingFiles")]
        public async Task MoveAudiobook_PathOnlyUpdate_RewritesStoredAbsoluteReferences()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            Init(services => services.WithSingleton(moveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-path-only-root");
            var sourcePath = FileService.GetTempDirectory("listenarr-path-only-source");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var unrelatedPath = Path.Join(FileService.GetTempPath(), "outside", "bonus.mp3");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Path Only Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Path Only",
                BasePath = sourcePath,
                FilePath = Path.Join(sourcePath, "book.m4b"),
                ImageUrl = Path.Join(sourcePath, "cover.jpg"),
                Files =
                [
                    new AudiobookFile { Path = Path.Join(sourcePath, "book.m4b") },
                    new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") },
                    new AudiobookFile { Path = unrelatedPath }
                ]
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath,
                MoveFiles = false
            });

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(targetPath, updated.BasePath);
            Assert.Equal(Path.Join(targetPath, "book.m4b"), updated.FilePath);
            Assert.Equal(Path.Join(targetPath, "cover.jpg"), updated.ImageUrl);
            Assert.Contains(updated.Files!, file => file.Path == Path.Join(targetPath, "book.m4b"));
            Assert.Contains(updated.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
            Assert.Contains(updated.Files!, file => file.Path == unrelatedPath);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "SetsBasePathWhenNoPriorBaseExists")]
        public async Task MoveAudiobook_PathOnlyUpdate_WithoutSourceBase_PreservesUnrelatedReferences()
        {
            var rootPath = FileService.GetTempDirectory("listenarr-path-only-empty-root");
            var targetPath = Path.Join(rootPath, "Author", "Title");
            var legacyPath = Path.Join(FileService.GetTempPath(), "legacy", "book.m4b");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Path Only Empty Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "No Source Base",
                FilePath = legacyPath,
                Files = [new AudiobookFile { Path = legacyPath }]
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath,
                MoveFiles = false
            });

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(targetPath, updated.BasePath);
            Assert.Equal(legacyPath, updated.FilePath);
            Assert.Equal(legacyPath, Assert.Single(updated.Files!).Path);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsAbsoluteDestinationInsideConfiguredOutputPath")]
        public async Task MoveAudiobook_AllowsAbsoluteDestinationInsideConfiguredOutputPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var target = Path.Join(outputPath, "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(target), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsAbsoluteDestinationOutsideConfiguredRoots")]
        public async Task MoveAudiobook_RejectsAbsoluteDestinationOutsideConfiguredRoots()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var originalBasePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(originalBasePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var outsidePath = Path.Join(FileService.GetTempDirectory("listenarr-move-outside"), "Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = outsidePath, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("configured root folder or output path", badObj.Value?.ToString() ?? string.Empty);

            var unchanged = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(unchanged);
            Assert.Equal(originalBasePath, unchanged.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "UsesDefaultRootFolderForRelativeDestination_WhenOutputPathEmpty")]
        public async Task MoveAudiobook_UsesDefaultRootFolderForRelativeDestination_WhenOutputPathEmpty()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(string.Empty)
                .Build());
            var rootPath = FileService.GetTempDirectory("listenarr-move-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Default Move Root")
                .WithPath(rootPath)
                .WithIsDefault()
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(FileService.GetTempDirectory("listenarr-move-src"))
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var relativeTarget = Path.Join("Author", "Title");
            var request = new LibraryController.MoveRequest { DestinationPath = relativeTarget, MoveFiles = false };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);

            var updated = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(updated);
            Assert.Equal(FileUtils.NormalizeStoredPath(Path.Join(rootPath, relativeTarget)), updated.BasePath);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsDestinationPathWithLeadingWhitespaceBeforeAbsolutePath")]
        public async Task MoveAudiobook_RejectsDestinationPathWithLeadingWhitespaceBeforeAbsolutePath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = " " + Path.Join(outputPath, "target"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("leading whitespace", badObj.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsCaseOnlyDestinationDifference_OnCaseSensitiveHosts")]
        public async Task MoveAudiobook_AllowsCaseOnlyDestinationDifference_OnCaseSensitiveHosts()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var mockMoveQueue = new Mock<IMoveQueueService>();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);

            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var controller = _provider.GetRequiredService<LibraryController>();

            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = Path.Join(outputPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var targetPath = Path.Join(outputPath, "caseonlybook");
            var request = new LibraryController.MoveRequest { DestinationPath = targetPath };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var acceptedObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, acceptedObj.StatusCode);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(targetPath)
                    && command.SourcePath == sourcePath
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == outputPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "TreatsCaseOnlyDestinationAsIdentical_OnCaseInsensitiveRoot")]
        public async Task MoveAudiobook_TreatsCaseOnlyDestinationAsIdentical_OnCaseInsensitiveRoot()
        {
            var mockMoveQueue = new Mock<IMoveQueueService>();
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-insensitive-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Insensitive Move Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .WithIsDefault()
                .Build());

            var sourcePath = Path.Join(rootPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join(rootPath, "caseonlybook")
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<BadRequestObjectResult>(result);
            Assert.Contains("identical", badObj.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "AllowsCaseOnlyDestination_OnExplicitlyCaseSensitiveRoot")]
        public async Task MoveAudiobook_AllowsCaseOnlyDestination_OnExplicitlyCaseSensitiveRoot()
        {
            var mockMoveQueue = new Mock<IMoveQueueService>();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedId);
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var rootPath = FileService.GetTempDirectory("listenarr-move-sensitive-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Sensitive Move Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .WithIsDefault()
                .Build());

            var sourcePath = Path.Join(rootPath, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());
            var targetPath = Path.Join(rootPath, "caseonlybook");
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.EnqueueMove(audiobook.Id, new LibraryController.MoveRequest
            {
                DestinationPath = targetPath
            });

            Assert.IsType<AcceptedResult>(result);
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.AudiobookId == audiobook.Id
                    && command.TargetPath == FileUtils.NormalizeStoredPath(targetPath)
                    && command.SourcePath == sourcePath
                    && command.DeleteEmptySource
                    && command.SourceCleanupBoundary == rootPath
                    && command.SourceIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    && command.TargetIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MoveAudiobook_NestedExplicitRoot_OverridesBroaderOutputPathSemantics()
        {
            var mockMoveQueue = new Mock<IMoveQueueService>();
            mockMoveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(mockMoveQueue.Object));
            var outputPath = FileService.GetTempDirectory("listenarr-move-nested-output");
            var sensitiveRoot = Path.Join(outputPath, "Sensitive Library");
            Directory.CreateDirectory(sensitiveRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Nested Sensitive Root")
                .WithPath(sensitiveRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());

            var sourcePath = Path.Join(sensitiveRoot, "CaseOnlyBook");
            Directory.CreateDirectory(sourcePath);
            var targetPath = Path.Join(sensitiveRoot, "caseonlybook");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested sensitive move")
                .WithBasePath(sourcePath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>().EnqueueMove(
                audiobook.Id,
                new LibraryController.MoveRequest { DestinationPath = targetPath });

            Assert.IsType<AcceptedResult>(result);
            mockMoveQueue.Verify(service => service.EnqueueMoveAsync(
                It.Is<MoveEnqueueCommand>(command =>
                    command.SourceIdentity.BoundaryPath == sensitiveRoot
                    && command.TargetIdentity.BoundaryPath == sensitiveRoot
                    && command.SourceIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                    && command.TargetIdentity.CaseSensitivity == FileSystemCaseSensitivity.Sensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "EnqueueMove")]
        [Trait("Scenario", "RejectsRelativeDestinationOutsideOutputPath")]
        public async Task MoveAudiobook_RejectsRelativeDestinationOutsideOutputPath()
        {
            var outputPath = FileService.GetTempDirectory("listenarr-move-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputPath)
                .Build());

            var sourcePath = FileService.GetTempDirectory("listenarr-move-src");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .WithBasePath(sourcePath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var request = new LibraryController.MoveRequest
            {
                DestinationPath = Path.Join("..", "escape"),
                MoveFiles = false
            };

            var result = await controller.EnqueueMove(audiobook.Id, request);

            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("DestinationPath", badObj.Value?.ToString() ?? string.Empty);
        }
    }
}
