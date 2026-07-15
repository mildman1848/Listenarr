using System.Text.Json;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMetadataRescanWorkflow
{
    private async Task<MetadataRescanApplyResult> ApplyMetadataRescanResultAsync(
        int audiobookId,
        AudibleBookMetadata metadata,
        string expectedMetadataState)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var audiobook = await repository.GetByIdAsync(audiobookId);
        if (audiobook == null)
        {
            return new MetadataRescanApplyResult(MetadataRescanApplyStatus.NotFound);
        }

        if (!string.Equals(
                CreateMetadataStateFingerprint(audiobook),
                expectedMetadataState,
                StringComparison.Ordinal))
        {
            return new MetadataRescanApplyResult(MetadataRescanApplyStatus.Conflict);
        }

        var legacyIdentifierFieldsTouched = ApplyMetadataRescanPatch(audiobook, metadata);
        if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
        {
            audiobook.ImageUrl = await MoveMetadataImageToLibraryStorageAsync(
                audiobook,
                metadata.ImageUrl) ?? metadata.ImageUrl;
        }

        if (legacyIdentifierFieldsTouched)
        {
            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);
        }

        await repository.UpdateAsync(audiobook);
        return new MetadataRescanApplyResult(
            MetadataRescanApplyStatus.Applied,
            audiobook);
    }

    private static string CreateMetadataStateFingerprint(Audiobook audiobook) =>
        JsonSerializer.Serialize(new
        {
            audiobook.Title,
            audiobook.Subtitle,
            audiobook.PublishYear,
            audiobook.PublishedDate,
            audiobook.Description,
            audiobook.Publisher,
            audiobook.Language,
            audiobook.Runtime,
            audiobook.Version,
            audiobook.Series,
            audiobook.SeriesNumber,
            audiobook.Authors,
            audiobook.Narrators,
            audiobook.Genres,
            audiobook.Isbn,
            audiobook.Asin,
            audiobook.OpenLibraryId,
            audiobook.ImageUrl,
            SeriesMemberships = audiobook.SeriesMemberships?
                .OrderBy(membership => membership.Id)
                .Select(membership => new
                {
                    membership.Id,
                    membership.SeriesName,
                    membership.SeriesAsin,
                    membership.SeriesNumber,
                    membership.IsPrimary,
                    membership.SortOrder
                }),
            ExternalIdentifiers = audiobook.ExternalIdentifiers?
                .OrderBy(identifier => identifier.Id)
                .Select(identifier => new
                {
                    identifier.Id,
                    identifier.Type,
                    identifier.ValueRaw,
                    identifier.ValueNormalized,
                    identifier.Region,
                    identifier.IsPrimary,
                    identifier.Source
                })
        });

    private enum MetadataRescanApplyStatus
    {
        Applied,
        NotFound,
        Conflict
    }

    private sealed record MetadataRescanApplyResult(
        MetadataRescanApplyStatus Status,
        Audiobook? Audiobook = null);
}
