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
    [Trait("Name", "LibraryController_ScanPathValidationTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_ScanPathValidationTests : BaseTests
    {
        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "AllowsRequestPathWithinConfiguredRoot_ReturnsOk")]
        public async Task ScanAudiobook_AllowsRequestPathWithinConfiguredRoot_ReturnsOk()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await _rootFolderRepository.AddAsync(new RootFolder { Name = "root", Path = tempRoot });
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(FileService.GetTempPath()).Build());
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            var result = await controller.ScanAudiobookFiles(ab.Id, new LibraryController.ScanRequest { Path = tempRoot });
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            Assert.Contains("No files found", ok.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        public async Task ScanAudiobook_PersistsBasePathBeforeClaimingRelativeFileOwnership()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-scan-basepath-order");
            var audioPath = await FileService.GetFileAsync(tempRoot, "Test.m4b");
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            Init(services => services.Without<IScanQueueService>().WithSingleton<IAudiobookFileService>(fileService.Object));
            var controller = _provider.GetRequiredService<LibraryController>();
            await _rootFolderRepository.AddAsync(new RootFolder { Name = "root", Path = tempRoot });
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(tempRoot).Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            fileService.Setup(service => service.ClaimAudiobookFileAsync(It.IsAny<Audiobook>(), It.IsAny<AudiobookFile>(), audioPath, It.IsAny<CancellationToken>()))
                .Returns<Audiobook, AudiobookFile, string, CancellationToken>(async (claimedAudiobook, _, _, token) =>
                {
                    var persisted = await _audiobookRepository.GetByIdSnapshotAsync(claimedAudiobook.Id, token);
                    Assert.NotNull(persisted);
                    Assert.Equal(Path.GetFullPath(tempRoot), Path.GetFullPath(persisted!.BasePath!));
                    return new AudiobookFileClaimResult(AudiobookFileClaimOutcome.IdentityUnavailable, Reason: "Injected claim result.");
                });
            var result = await controller.ScanAudiobookFiles(audiobook.Id, new LibraryController.ScanRequest { Path = tempRoot });
            Assert.IsType<OkObjectResult>(result);
            fileService.Verify(service => service.ClaimAudiobookFileAsync(It.IsAny<Audiobook>(), It.IsAny<AudiobookFile>(), audioPath, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ScanAudiobook_ExistingAbsoluteOwnershipRow_IsNotRemovedAsMissing()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-scan-absolute-row");
            var audioPath = await FileService.GetFileAsync(tempRoot, "Test.m4b");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await _rootFolderRepository.AddAsync(new RootFolder { Name = "root", Path = tempRoot });
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(tempRoot).Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").WithBasePath(tempRoot).Build());
            var resolution = await _provider.GetRequiredService<IFileSystemSemanticsResolver>().ResolveAsync(tempRoot);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var identity = AudiobookFilePathIdentity.CreateValid(audioPath, resolution.Semantics, FileSystemCaseSensitivityMode.Auto, resolution.BoundaryPath);
            var existingFile = AudiobookFile.CreateUnresolved(audioPath);
            existingFile.AudiobookId = audiobook.Id;
            existingFile.ApplyPathIdentity(audioPath, identity);
            await _audiobookFileRepository.AddAsync(existingFile);
            var result = await controller.ScanAudiobookFiles(audiobook.Id, new LibraryController.ScanRequest { Path = tempRoot });
            Assert.IsType<OkObjectResult>(result);
            var retained = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal(existingFile.Id, retained.Id);
            Assert.Equal(audioPath, retained.Path);
        }

        [Fact]
        public async Task ScanAudiobook_SymlinkedDirectoryOutsideRoot_IsNotTraversed()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var tempRoot = FileService.GetTempDirectory("listenarr-scan-link-root");
            var outsideRoot = FileService.GetTempDirectory("listenarr-scan-link-outside");
            var outsideFile = await FileService.GetFileAsync(outsideRoot, "Test.m4b", "audio");
            var linkedDirectory = Path.Join(tempRoot, "linked");
            Directory.CreateSymbolicLink(linkedDirectory, outsideRoot);
            Assert.True(File.Exists(Path.Join(linkedDirectory, Path.GetFileName(outsideFile))));

            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "root",
                Path = tempRoot
            });
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .Build());

            var result = await controller.ScanAudiobookFiles(
                audiobook.Id,
                new LibraryController.ScanRequest { Path = tempRoot });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("No files found", ok.Value?.ToString() ?? string.Empty);
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest")]
        public async Task ScanAudiobook_RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");
            var other = FileService.GetTempDirectory("listenarr-other");
            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();
            await _rootFolderRepository.AddAsync(new RootFolder { Name = "root", Path = tempRoot });
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder().WithOutputPath(Path.Join(FileService.GetTempPath(), "different-root")).Build());
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithTitle("Test").Build());
            var result = await controller.ScanAudiobookFiles(ab.Id, new LibraryController.ScanRequest { Path = other });
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, bad.StatusCode);
            Assert.Contains("not within configured root folders", bad.Value?.ToString() ?? string.Empty);
        }
    }
}
