using System.Text.Json;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private const string RelocationReservationMarkerName =
        ".listenarr-relocation-directory.json";
    private static readonly JsonSerializerOptions ReservationJsonOptions =
        new(JsonSerializerDefaults.Web);
    internal Action<string>? TargetReservationDirectoryFlushedForTest
    {
        get;
        set;
    }
    internal Action<string>? BeforeReservationMarkerRetirementForTest
    {
        get;
        set;
    }
    internal Action<string>? AfterReservationMarkerRetiredForTest
    {
        get;
        set;
    }

    private async Task<DirectoryObjectIdentityResolution>
        ReserveRelocationTargetAsync(
            Guid relocationId,
            string targetPath,
            CancellationToken cancellationToken)
    {
        await using (var existingDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var firstExisting = await existingDb
                .RootFolderRelocationCreatedDirectories
                .AsNoTracking()
                .Where(candidate =>
                    candidate.RelocationId == relocationId)
                .OrderBy(candidate => candidate.CanonicalPath.Length)
                .FirstOrDefaultAsync(cancellationToken);
            if (firstExisting != null)
            {
                var persistedAncestor = Path.GetDirectoryName(
                    firstExisting.CanonicalPath)
                    ?? throw new InvalidOperationException(
                        "A persisted relocation target reservation has no parent.");
                return await CreateOrReuseTargetReservationsAsync(
                    relocationId,
                    persistedAncestor,
                    cancellationToken);
            }
        }

        var plan = DiscoverTargetReservationPlan(targetPath);
        if (plan.Segments.Count == 0)
        {
            return await ResolveExistingDirectoryObjectIdentityAsync(
                targetPath,
                cancellationToken);
        }

        await PersistTargetReservationPlanAsync(
            relocationId,
            plan,
            cancellationToken);
        return await CreateOrReuseTargetReservationsAsync(
            relocationId,
            plan.ExistingAncestor,
            cancellationToken);
    }

    private async Task ReconcileRelocationTargetReservationsAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderByDescending(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reservation.State is
                RootFolderRelocationCreatedDirectoryState.Removed
                    or RootFolderRelocationCreatedDirectoryState.Retained)
            {
                continue;
            }

            var parentPath = Path.GetDirectoryName(
                reservation.CanonicalPath)
                ?? throw new InvalidOperationException(
                    "A relocation directory reservation has no parent.");
            using var parent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reservation.CanonicalPath));
            if (publication == null)
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Removed;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            using var directory =
                publication.OpenCreatedDirectoryAnchor();
            if (reservation.State ==
                RootFolderRelocationCreatedDirectoryState.Planned)
            {
                if (!TryEnrollPublishedPlannedReservation(
                        relocationId,
                        reservation,
                        parent,
                        directory))
                {
                    reservation.State =
                        RootFolderRelocationCreatedDirectoryState.Retained;
                    reservation.UpdatedAt =
                        timeProvider.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Created;
                reservation.DirectoryObjectIdentityVersion = 1;
                reservation.DirectoryObjectIdentity =
                    directory.GetDirectoryObjectIdentity();
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
            }

            ValidateReservationDirectory(
                relocationId,
                reservation,
                directory);
            var entries = Directory.EnumerateFileSystemEntries(
                    reservation.CanonicalPath)
                .Take(2)
                .ToList();
            var markerPath = Path.Join(
                reservation.CanonicalPath,
                RelocationReservationMarkerName);
            if (entries.Count != 1
                || !string.Equals(
                    entries[0],
                    markerPath,
                    PathComparison)
                || !directory.VisiblePathMatches()
                || !parent.VisiblePathMatches())
            {
                reservation.State =
                    RootFolderRelocationCreatedDirectoryState.Retained;
                reservation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            using (var marker = directory.OpenExistingFile(
                RelocationReservationMarkerName,
                requireDeleteAccess: true))
            {
                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                if (!marker.VisiblePathMatches()
                    || !directory.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A relocation reservation marker changed before cleanup.");
                }

                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                marker.Delete();
            }

            if (Directory.EnumerateFileSystemEntries(
                    reservation.CanonicalPath).Any()
                || !directory.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A relocation-created directory changed after marker retirement.");
            }

            publication.DeletePinnedEmptyDirectory(
                Path.GetFileName(reservation.CanonicalPath));
            reservation.State =
                RootFolderRelocationCreatedDirectoryState.Removed;
            reservation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task FinalizeRelocationTargetReservationsAsync(
        ListenArrDbContext db,
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reservation.State ==
                RootFolderRelocationCreatedDirectoryState.Retained)
            {
                continue;
            }

            if (reservation.State !=
                RootFolderRelocationCreatedDirectoryState.Created)
            {
                throw new InvalidOperationException(
                    "A successful relocation has an incomplete target directory reservation.");
            }

            var parentPath = Path.GetDirectoryName(
                reservation.CanonicalPath)
                ?? throw new InvalidOperationException(
                    "A relocation directory reservation has no parent.");
            using var parent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            using var publication =
                parent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reservation.CanonicalPath))
                ?? throw new InvalidOperationException(
                    "A relocation-created target directory disappeared before finalization.");
            using var directory =
                publication.OpenCreatedDirectoryAnchor();
            ValidateReservationDirectoryIdentity(
                reservation,
                directory);

            using var marker = directory.TryOpenExistingFile(
                RelocationReservationMarkerName,
                requireDeleteAccess: false);
            if (marker != null)
            {
                ValidateReservationMarker(
                    relocationId,
                    reservation,
                    marker);
                if (!marker.VisiblePathMatches()
                    || !directory.VisiblePathMatches()
                    || !parent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "A relocation reservation marker changed before finalization.");
                }
            }

            if (!directory.VisiblePathMatches()
                || !parent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A relocation-created target directory changed during finalization.");
            }

            reservation.State =
                RootFolderRelocationCreatedDirectoryState.Retained;
            reservation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private static TargetReservationPlan DiscoverTargetReservationPlan(
        string targetPath)
    {
        var canonicalTarget = Path.GetFullPath(targetPath);
        var missing = new Stack<string>();
        var current = canonicalTarget;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new InvalidOperationException(
                    "A relocation target segment is occupied by a file.");
            }

            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    "The relocation target has no existing directory ancestor.");
        }

        using var ancestor =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                current,
                createMissing: false);
        if (!ancestor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The relocation target ancestor changed during planning.");
        }

        return new TargetReservationPlan(
            ancestor.FullPath,
            ancestor.GetDirectoryObjectIdentity(),
            missing.ToList());
    }

    private async Task<DirectoryObjectIdentityResolution>
        CreateOrReuseTargetReservationsAsync(
            Guid relocationId,
            string existingAncestor,
            CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservations = await db.RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync(cancellationToken);
        var current =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                existingAncestor,
                createMissing: false);
        try
        {
            foreach (var reservation in reservations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reservation.State is
                    RootFolderRelocationCreatedDirectoryState.Retained
                        or RootFolderRelocationCreatedDirectoryState.Removed)
                {
                    throw new InvalidOperationException(
                        "A terminal relocation target reservation cannot be reused.");
                }

                var parentIdentity =
                    current.GetDirectoryObjectIdentity();
                if (reservation.State ==
                    RootFolderRelocationCreatedDirectoryState.Planned)
                {
                    if (reservation.DirectoryObjectIdentityVersion == null
                        && string.IsNullOrWhiteSpace(
                            reservation.DirectoryObjectIdentity))
                    {
                        reservation.DirectoryObjectIdentityVersion = 1;
                        reservation.DirectoryObjectIdentity =
                            parentIdentity;
                        reservation.UpdatedAt =
                            timeProvider.GetUtcNow().UtcDateTime;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    else if (reservation.DirectoryObjectIdentityVersion != 1
                        || !string.Equals(
                            reservation.DirectoryObjectIdentity,
                            parentIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The parent of a planned relocation directory was replaced before creation.");
                    }
                }

                var childName = Path.GetFileName(
                    reservation.CanonicalPath);
                using var creation =
                    current.TryCreateChildForPublication(childName);
                PinnedDirectoryCreation.PinnedDirectoryAnchor next;
                if (creation.Created)
                {
                    next = creation.OpenCreatedDirectoryAnchor();
                    WriteReservationMarker(
                        relocationId,
                        reservation,
                        next);
                    FlushReservationDirectory(next);
                    FlushReservationDirectory(current);
                }
                else
                {
                    next = current.OpenExistingChild(childName);
                    ValidateReservationMarker(
                        relocationId,
                        reservation,
                        next);
                }

                try
                {
                    var liveIdentity =
                        next.GetDirectoryObjectIdentity();
                    if (reservation.State ==
                            RootFolderRelocationCreatedDirectoryState.Created
                        && (reservation.DirectoryObjectIdentityVersion != 1
                            || !string.Equals(
                                reservation.DirectoryObjectIdentity,
                                liveIdentity,
                                StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "A relocation-created directory was replaced after enrollment.");
                    }

                    reservation.State =
                        RootFolderRelocationCreatedDirectoryState.Created;
                    reservation.DirectoryObjectIdentityVersion = 1;
                    reservation.DirectoryObjectIdentity = liveIdentity;
                    reservation.UpdatedAt =
                        timeProvider.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(cancellationToken);
                    current.Dispose();
                    current = next;
                    next = null!;
                }
                finally
                {
                    next?.Dispose();
                }
            }

            if (!current.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The reserved relocation target changed before use.");
            }

            return new DirectoryObjectIdentityResolution(
                1,
                current.GetDirectoryObjectIdentity(),
                null);
        }
        finally
        {
            current.Dispose();
        }
    }

    private async Task MarkPrecommittedRelocationNeedsAttentionAsync(
        Guid relocationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var recoveryDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await recoveryDb.RootFolderRelocations
            .SingleAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken);
        if (persisted.Status is
            RootFolderRelocationStatus.Pending
                or RootFolderRelocationStatus.Running)
        {
            persisted.Status =
                RootFolderRelocationStatus.NeedsAttention;
            persisted.Error =
                $"Target directory reservation requires attention: {exception.Message}";
            persisted.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await recoveryDb.SaveChangesAsync(cancellationToken);
        }
    }

}
