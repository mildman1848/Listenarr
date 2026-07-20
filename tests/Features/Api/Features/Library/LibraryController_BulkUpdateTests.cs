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
using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "LibraryController_BulkUpdateTests")]
    [Trait("Category", "LibraryController")]
    public sealed class LibraryController_BulkUpdateTests : BaseTests
    {
        [Fact]
        public async Task BulkUpdate_InvalidRootFolderStillAppliesValidMetadataUpdates()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Partial Bulk Update",
                Monitored = false
            });

            var actionResult = await controller.BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
            {
                Ids = [audiobook.Id],
                Updates = new Dictionary<string, object>
                {
                    ["monitored"] = true,
                    ["rootFolder"] = "   "
                }
            });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.NotEmpty(result.GetProperty("errors").EnumerateArray());

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
        }

        [Fact]
        public async Task BulkUpdate_CustomRootOutsideConfiguredBoundaries_IsRejectedWithoutPathRewrite()
        {
            var configuredRoot = FileService.GetTempDirectory("bulk-configured-root");
            var outsideRoot = FileService.GetTempDirectory("bulk-outside-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var originalBasePath = Path.Join(configuredRoot, "Existing", "Book");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Boundary Test",
                Authors = ["Author"],
                Monitored = false,
                BasePath = originalBasePath
            });

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true,
                        ["rootFolder"] = outsideRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains(
                    "configured root folder or output path",
                    StringComparison.OrdinalIgnoreCase) == true);

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
            Assert.Equal(originalBasePath, stored.BasePath);
        }

        [Fact]
        public async Task BulkUpdate_ApplyRootMonitoredQuality_ReturnsPerIdResultsAndPersistsChanges()
        {
            // Arrange
            var controller = _provider.GetRequiredService<LibraryController>();

            var tempRoot = FileService.GetTempDirectory("bulk-update");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());

            await _qualityProfileRepository.AddAsync(new QualityProfile
            {
                Id = 42,
                Name = "Test Profile",
                Qualities = new List<QualityDefinition>(),
                PreferredFormats = new List<string>(),
                PreferredLanguages = new List<string>(),
                MustContain = new List<string>(),
                MustNotContain = new List<string>()
            });

            var sourceBasePath = Path.Join(
                FileService.GetTempPath(),
                $"bulk-update-source-{Guid.NewGuid():N}");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            var sourceImagePath = Path.Join(sourceBasePath, "cover.jpg");
            var a1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book A",
                Authors = new List<string> { "Author A" },
                Monitored = false,
                QualityProfileId = null,
                BasePath = sourceBasePath,
                FilePath = sourceFilePath,
                ImageUrl = sourceImagePath,
                Files =
                [
                    new AudiobookFile
                    {
                        Path = sourceFilePath
                    }
                ]
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book B",
                Authors = new List<string> { "Author B" },
                Monitored = false,
                QualityProfileId = null
            });

            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = new List<int> { a1.Id, 999999 },
                Updates = new Dictionary<string, object>
                {
                    { "monitored", true },
                    { "qualityProfileId", 42 },
                    { "rootFolder", tempRoot }
                }
            };

            // Act
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("results", out var resultsElem));
            Assert.Equal(2, resultsElem.GetArrayLength());

            var first = resultsElem[0];
            Assert.Equal(a1.Id, first.GetProperty("id").GetInt32());
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("errors").GetArrayLength() == 0);

            var second = resultsElem[1];
            Assert.Equal(999999, second.GetProperty("id").GetInt32());
            Assert.False(second.GetProperty("success").GetBoolean());
            Assert.True(second.GetProperty("errors").GetArrayLength() >= 1);

            var storedA1 = await GetFreshAudiobookAsync(a1.Id);
            Assert.NotNull(storedA1);
            Assert.True(storedA1.Monitored);
            Assert.Equal(42, storedA1.QualityProfileId);
            Assert.False(string.IsNullOrWhiteSpace(storedA1.BasePath));
            Assert.StartsWith(FileUtils.NormalizeStoredPath(tempRoot), storedA1.BasePath);
            Assert.Contains("Author A", storedA1.BasePath);
            Assert.Contains("Book A", storedA1.BasePath);
            Assert.Equal(Path.Join(storedA1.BasePath, "book.m4b"), storedA1.FilePath);
            Assert.Equal(Path.Join(storedA1.BasePath, "cover.jpg"), storedA1.ImageUrl);
            var storedFile = Assert.Single(storedA1.Files!);
            Assert.Equal(Path.Join(storedA1.BasePath, "book.m4b"), storedFile.Path);

            var histories = await _historyRepository.GetByAudiobookIdAsync(a1.Id);
            Assert.True(histories.Count >= 1);
        }

        private async Task<Audiobook?> GetFreshAudiobookAsync(int id)
        {
            using var scope = _provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            return await repository.GetByIdAsync(id);
        }
    }
}
