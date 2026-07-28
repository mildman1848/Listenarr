using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private async Task PersistTargetReservationPlanAsync(
        Guid relocationId,
        TargetReservationPlan plan,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        _ = await db.RootFolderRelocations.SingleOrDefaultAsync(
            candidate => candidate.Id == relocationId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The relocation must be durably committed before target reservation.");
        var existing = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        if (existing.Count == 0)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            for (var index = 0; index < plan.Segments.Count; index++)
            {
                db.RootFolderRelocationCreatedDirectories.Add(new()
                {
                    RelocationId = relocationId,
                    CanonicalPath = plan.Segments[index],
                    OwnershipToken = Guid.NewGuid().ToString("N"),
                    State =
                        RootFolderRelocationCreatedDirectoryState.Planned,
                    DirectoryObjectIdentityVersion =
                        index == 0 ? 1 : null,
                    DirectoryObjectIdentity =
                        index == 0
                            ? plan.ExistingAncestorIdentity
                            : null,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        else if (existing.Count != plan.Segments.Count
            || existing.Zip(plan.Segments).Any(pair =>
                !string.Equals(
                    Path.GetFullPath(pair.First.CanonicalPath),
                    Path.GetFullPath(pair.Second),
                    PathComparison)
                || !Guid.TryParseExact(
                    pair.First.OwnershipToken,
                    "N",
                    out _)))
        {
            throw new InvalidOperationException(
                "The persisted relocation target reservation generation does not match the requested target.");
        }

        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void WriteReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        using var marker = directory.CreateNewFile(
            RelocationReservationMarkerName,
            hiddenFile: true);
        using (var stream = marker.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new TargetReservationMarker(
                        1,
                        relocationId,
                        reservation.OwnershipToken,
                        reservation.CanonicalPath),
                    ReservationJsonOptions));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!marker.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The relocation reservation marker changed during publication.");
        }
    }

    private static void ValidateReservationDirectory(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        ValidateReservationDirectoryIdentity(reservation, directory);
        ValidateReservationMarker(
            relocationId,
            reservation,
            directory);
    }

    private static void ValidateReservationDirectoryIdentity(
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (reservation.State is not (
                RootFolderRelocationCreatedDirectoryState.Created
                    or RootFolderRelocationCreatedDirectoryState.Retained)
            || reservation.DirectoryObjectIdentityVersion != 1
            || string.IsNullOrWhiteSpace(
                reservation.DirectoryObjectIdentity)
            || !string.Equals(
                reservation.DirectoryObjectIdentity,
                directory.GetDirectoryObjectIdentity(),
                StringComparison.Ordinal)
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation directory reservation lacks matching physical identity.");
        }
    }

    private static void ValidateReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        using var marker = directory.OpenExistingFile(
            RelocationReservationMarkerName,
            requireDeleteAccess: false);
        ValidateReservationMarker(
            relocationId,
            reservation,
            marker);
        if (!marker.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A relocation reservation marker changed during validation.");
        }
    }

    private static void ValidateReservationMarker(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedFileEntry marker)
    {
        TargetReservationMarker? payload;
        using (var stream = marker.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            if (stream.Length <= 0 || stream.Length > 16 * 1024)
            {
                throw new InvalidOperationException(
                    "A relocation reservation marker has an invalid size.");
            }

            payload = JsonSerializer.Deserialize<TargetReservationMarker>(
                stream,
                ReservationJsonOptions);
        }

        if (payload == null
            || payload.Version != 1
            || payload.RelocationId != relocationId
            || !string.Equals(
                payload.OwnershipToken,
                reservation.OwnershipToken,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFullPath(payload.CanonicalPath),
                Path.GetFullPath(reservation.CanonicalPath),
                PathComparison))
        {
            throw new InvalidOperationException(
                "A relocation reservation marker belongs to another generation.");
        }
    }

    private void FlushReservationDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        directory.FlushDirectoryEntry();
        TargetReservationDirectoryFlushedForTest?.Invoke(
            directory.FullPath);
    }

    private static bool TryEnrollPublishedPlannedReservation(
        Guid relocationId,
        RootFolderRelocationCreatedDirectory reservation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        try
        {
            if (reservation.DirectoryObjectIdentityVersion != 1
                || string.IsNullOrWhiteSpace(
                    reservation.DirectoryObjectIdentity)
                || !string.Equals(
                    reservation.DirectoryObjectIdentity,
                    parent.GetDirectoryObjectIdentity(),
                    StringComparison.Ordinal)
                || !parent.VisiblePathMatches()
                || !directory.VisiblePathMatches())
            {
                return false;
            }

            ValidateReservationMarker(
                relocationId,
                reservation,
                directory);
            return parent.VisiblePathMatches()
                && directory.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException
                or StackOverflowException))
        {
            return false;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record TargetReservationPlan(
        string ExistingAncestor,
        string ExistingAncestorIdentity,
        IReadOnlyList<string> Segments);

    private sealed record TargetReservationMarker(
        int Version,
        Guid RelocationId,
        string OwnershipToken,
        string CanonicalPath);
}
