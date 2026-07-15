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
using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog
{
    public class LibraryAddService : ILibraryAddService
    {
        private readonly IAudiobookRepository _repo;
        private readonly IHistoryRepository _historyRepository;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryAddService> _logger;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly AudibleService _audibleService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly ILibraryDestinationMutationGuard _destinationMutationGuard;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly INotificationService? _notificationService;

        public LibraryAddService(
            IAudiobookRepository repo,
            IHistoryRepository historyRepository,
            IImageCacheService imageCacheService,
            ILogger<LibraryAddService> logger,
            IQualityProfileService qualityProfileService,
            AudibleService audibleService,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IRootFolderService rootFolderService,
            ILibraryDestinationMutationGuard destinationMutationGuard,
            IFilesystemMutationCoordinator mutationCoordinator,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _historyRepository = historyRepository;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _qualityProfileService = qualityProfileService;
            _audibleService = audibleService;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _rootFolderService = rootFolderService;
            _destinationMutationGuard = destinationMutationGuard
                ?? throw new ArgumentNullException(nameof(destinationMutationGuard));
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _notificationService = notificationService;
        }

        public Task<LibraryAddOperationResult> AddToLibraryAsync(
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken = default) =>
            _mutationCoordinator.ExecuteExclusiveAsync(
                token => AddToLibraryCoreAsync(request, token),
                cancellationToken);

        private async Task<LibraryAddOperationResult> AddToLibraryCoreAsync(
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var metadata = request.Metadata ?? new AudibleBookMetadata();

            _logger.LogInformation(
                "LibraryAddService received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                metadata.Title,
                metadata.Asin,
                metadata.PublishYear,
                metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null",
                metadata.Series);

            if (string.IsNullOrWhiteSpace(metadata.PublishYear) && request.SearchResult != null)
            {
                try
                {
                    if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                    {
                        metadata.PublishYear = publishDate.Year.ToString();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
                }
            }

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                if (existingByAsin != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByAsin
                    };
                }
            }

            var firstIsbn = (metadata.Isbn ?? Enumerable.Empty<string>())
                .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                if (existingByIsbn != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByIsbn
                    };
                }
            }

            var audiobook = metadata.ToAudiobook();

            audiobook.Monitored = request.Monitored;

            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook, metadata.Region);

            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
            }
            else
            {
                var defaultProfile = await _qualityProfileService.GetDefaultAsync();
                if (defaultProfile != null)
                {
                    audiobook.QualityProfileId = defaultProfile.Id;
                }
                else
                {
                    _logger.LogWarning(
                        "No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.",
                        audiobook.Title);
                }
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();

            var requestedBaseDirectory = request.DestinationPath;
            if (!string.IsNullOrWhiteSpace(requestedBaseDirectory))
            {
                // Preserve valid Unix path-segment whitespace, but reject values that only become
                // absolute after trimming accidental leading whitespace.
                if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(requestedBaseDirectory))
                {
                    return ValidationFailure("DestinationPath is invalid: leading whitespace before an absolute path is not allowed.");
                }

                if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                    requestedBaseDirectory,
                    out var normalizedRequestedBaseDirectory,
                    out var validationReason,
                    rejectParentTraversal: true))
                {
                    return ValidationFailure($"DestinationPath is invalid: {validationReason}");
                }

                audiobook.BasePath = normalizedRequestedBaseDirectory;
            }
            else
            {
                var rootFolder = await _rootFolderService.GetDefaultAsync();
                var baseDirectory = rootFolder != null ? rootFolder.Path : settings.OutputPath;

                // This validates the Listenarr-owned library destination. Do not use it for
                // download-client source paths, which must preserve the client's exact path identity.
                var generatedBasePath = Path.Join(baseDirectory, _fileNamingService.ApplyNamingPattern(settings.FolderNamingPattern, metadata));
                if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                    generatedBasePath,
                    out var normalizedGeneratedBasePath,
                    out var validationReason,
                    rejectParentTraversal: true))
                {
                    return ValidationFailure($"Generated library destination is invalid: {validationReason}");
                }

                audiobook.BasePath = normalizedGeneratedBasePath;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var destinationBlockingReason = await _destinationMutationGuard.GetBlockingReasonAsync(
                audiobook.BasePath!,
                cancellationToken);
            if (destinationBlockingReason != null)
            {
                return ValidationFailure(destinationBlockingReason);
            }

            audiobook.ImageUrl = await MoveImageToLibraryStorageAsync(
                metadata,
                request.SearchResult,
                firstIsbn);
            await _repo.AddAsync(audiobook);

            await ResolveAuthorAsinsAsync(audiobook);
            await SendAddedNotificationAsync(audiobook);
            await AddHistoryEntryAsync(audiobook, request, cancellationToken);

            _logger.LogInformation(
                "Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title,
                audiobook.Asin,
                request.Monitored,
                audiobook.QualityProfileId,
                request.AutoSearch);

            return new LibraryAddOperationResult
            {
                Added = true,
                Message = "Audiobook added to library successfully",
                Audiobook = audiobook
            };
        }

        private async Task<string?> MoveImageToLibraryStorageAsync(
            AudibleBookMetadata metadata,
            SearchResult? searchResult,
            string? firstIsbn)
        {
            var imageUrl = metadata.ImageUrl;

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                return await TryMoveImageAsync(metadata.Asin, metadata.ImageUrl) ?? imageUrl;
            }

            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var derivedKey = "img-" + ComputeShortHash(firstIsbn);
                return await TryMoveImageAsync(derivedKey, metadata.ImageUrl) ?? imageUrl;
            }

            if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
            {
                var rawKey = searchResult?.Id ?? searchResult?.ResultUrl ?? searchResult?.ProductUrl ?? metadata.ImageUrl;
                var derivedKey = "img-" + ComputeShortHash(rawKey);
                return await TryMoveImageAsync(derivedKey, metadata.ImageUrl) ?? imageUrl;
            }

            return imageUrl;
        }

        private async Task<string?> TryMoveImageAsync(string imageKey, string? sourceImageUrl)
        {
            try
            {
                var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(imageKey, sourceImageUrl);
                return string.IsNullOrWhiteSpace(libraryImagePath) ? null : $"/{libraryImagePath}";
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }

            return null;
        }

        private async Task ResolveAuthorAsinsAsync(Audiobook audiobook)
        {
            try
            {
                if (audiobook.Authors == null || !audiobook.Authors.Any())
                {
                    return;
                }

                audiobook.AuthorAsins = audiobook.AuthorAsins ?? new List<string>();
                foreach (var authorName in audiobook.Authors)
                {
                    try
                    {
                        var info = await _audibleService.LookupAuthorAsync(authorName);
                        if (info == null || string.IsNullOrWhiteSpace(info.Asin))
                        {
                            continue;
                        }

                        if (!audiobook.AuthorAsins.Contains(info.Asin))
                        {
                            audiobook.AuthorAsins.Add(info.Asin);
                        }

                        try
                        {
                            var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                            if (moved != null)
                            {
                                _logger.LogInformation(
                                    "Cached author image for {Author} (ASIN: {Asin})",
                                    authorName,
                                    info.Asin);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to cache author image for {Author}", authorName);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                    }
                }

                await _repo.UpdateAsync(audiobook);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving author ASINs for audiobook '{Title}'", audiobook.Title);
            }
        }

        private async Task SendAddedNotificationAsync(Audiobook audiobook)
        {
            if (_notificationService == null)
            {
                return;
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();
            var data = new
            {
                id = audiobook.Id,
                title = audiobook.Title ?? "Unknown Title",
                authors = audiobook.Authors,
                narrators = audiobook.Narrators,
                description = audiobook.Description,
                asin = audiobook.Asin,
                publisher = audiobook.Publisher,
                year = audiobook.PublishYear,
                imageUrl = audiobook.ImageUrl
            };

            await _notificationService.SendNotificationAsync(
                "book-added",
                data,
                settings.WebhookUrl,
                settings.EnabledNotificationTriggers);
        }

        private async Task AddHistoryEntryAsync(
            Audiobook audiobook,
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken)
        {
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown Title",
                EventType = "Added",
                Message = request.HistoryMessage ??
                    $"Audiobook '{audiobook.Title}' added to library from {request.HistorySource}",
                Source = request.HistorySource,
                Timestamp = DateTime.UtcNow
            };

            await _historyRepository.AddAsync(historyEntry, cancellationToken);
        }

        private static LibraryAddOperationResult ValidationFailure(string message) => new()
        {
            ValidationFailed = true,
            Message = message,
            ValidationMessage = message
        };

        private static string? ToStringOrFirst(object? value)
        {
            if (value is List<string> list)
            {
                return list.FirstOrDefault();
            }

            return value as string;
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Guid.NewGuid().ToString("N").Substring(0, 12);
            }

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

    }
}
