using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private async Task TrySendAddedNotificationAsync(Audiobook audiobook)
    {
        try
        {
            await SendAddedNotificationAsync(audiobook);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogWarning(
                ex,
                "Audiobook '{Title}' was added, but its notification could not be sent",
                audiobook.Title);
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

    private static History CreateHistoryEntry(
        Audiobook audiobook,
        LibraryAddOperationRequest request)
    {
        return new History
        {
            AudiobookId = audiobook.Id,
            AudiobookTitle = audiobook.Title ?? "Unknown Title",
            EventType = "Added",
            Message = request.HistoryMessage
                ?? $"Audiobook '{audiobook.Title}' added to library from {request.HistorySource}",
            Source = request.HistorySource,
            Timestamp = DateTime.UtcNow
        };
    }
}
