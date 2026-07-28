namespace Listenarr.Infrastructure.Library.Moving;

internal static class PinnedLibraryDirectoryOwnershipMarker
{
    public static async Task PublishMigrationTargetAsync(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var parentPath = Path.GetDirectoryName(target.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The migrated ownership path has no parent directory.");
        using var parent =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
        using var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(target.CanonicalPath));
        using var directory = publication.OpenCreatedDirectoryAnchor();
        if (target.DirectoryObjectIdentityVersion != 1
            || !string.Equals(
                target.DirectoryObjectIdentity,
                directory.GetDirectoryObjectIdentity(),
                StringComparison.Ordinal)
            || !parent.VisiblePathMatches()
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The migrated ownership target does not match the persisted physical directory generation.");
        }

        await PublishMigratedMarkerAsync(
            source,
            target,
            directory,
            LibraryDirectoryOwnershipMarker.FileName,
            cancellationToken);
        await PublishMigratedMarkerAsync(
            source,
            target,
            parent,
            $".listenarr-directory-owner-{target.OwnershipToken}.json",
            cancellationToken);
        directory.FlushDirectoryEntry();
        parent.FlushDirectoryEntry();
        if (!parent.VisiblePathMatches() || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The migrated ownership target changed during marker publication.");
        }
    }

    public static async Task EnsureAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation creation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(creation);
        LibraryDirectoryOwnershipMarker.ValidateOwnershipToken(ownership.OwnershipToken);
        if (!creation.Created || !creation.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The pinned directory is no longer reachable through its validated pathname.");
        }

        var payload = LibraryDirectoryOwnershipMarker.SerializePayload(ownership);
        using var directory = creation.OpenCreatedDirectoryAnchor();
        using var parent = creation.OpenParentDirectoryAnchor();
        await EnsureMarkerAsync(
            ownership,
            directory,
            LibraryDirectoryOwnershipMarker.FileName,
            payload,
            cancellationToken);
        await EnsureMarkerAsync(
            ownership,
            parent,
            $".listenarr-directory-owner-{ownership.OwnershipToken}.json",
            payload,
            cancellationToken);

        if (!creation.VisiblePathMatches()
            || !directory.VisiblePathMatches()
            || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The pinned directory pathname changed during ownership publication.");
        }
    }

    private static async Task EnsureMarkerAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        string payload,
        CancellationToken cancellationToken)
    {
        using var existing = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        if (existing != null)
        {
            LibraryDirectoryOwnershipMarker.ValidateMarkerFile(ownership, existing);
            return;
        }

        var temporaryName = fileName + ".v2.tmp";
        using var interruptedTemporary = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: true);
        if (interruptedTemporary != null)
        {
            var interruptedPayload =
                LibraryDirectoryOwnershipMarker.ReadPayload(interruptedTemporary);
            if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    ownership,
                    interruptedPayload))
            {
                throw new InvalidOperationException(
                    "A durable ownership marker temporary file is stale or mismatched.");
            }

            interruptedTemporary.MoveWithinParent(fileName);
            ValidateExistingMarker(ownership, parent, fileName);
            return;
        }

        try
        {
            await parent.PublishNewFileAsync(
                temporaryName,
                fileName,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                async stream =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                _ => false);
        }
        catch (Exception exception) when (
            (exception is IOException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            && parent.TryOpenExistingFile(fileName, requireDeleteAccess: false) is { } published)
        {
            using (published)
            {
                LibraryDirectoryOwnershipMarker.ValidateMarkerFile(
                    ownership,
                    published);
            }
            return;
        }

        ValidateExistingMarker(ownership, parent, fileName);
    }

    private static async Task PublishMigratedMarkerAsync(
        LibraryDirectoryOwnership source,
        LibraryDirectoryOwnership target,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var existing = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        if (existing != null)
        {
            var payload = LibraryDirectoryOwnershipMarker.ReadPayload(existing);
            if (LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    target,
                    payload))
            {
                return;
            }
            if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    source,
                    payload))
            {
                throw new InvalidOperationException(
                    "The ownership migration target contains an unrelated marker.");
            }
        }

        var temporaryName = fileName + ".migration.tmp";
        using var interrupted = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: true);
        if (interrupted != null)
        {
            var interruptedPayload =
                LibraryDirectoryOwnershipMarker.ReadPayload(interrupted);
            if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    target,
                    interruptedPayload))
            {
                throw new InvalidOperationException(
                    "The ownership migration temporary marker is unrelated.");
            }

            if (existing != null)
            {
                interrupted.ReplaceWithinParent(fileName, existing);
            }
            else
            {
                interrupted.MoveWithinParent(fileName);
            }
            return;
        }

        using var temporary = parent.CreateNewFile(
            temporaryName,
            hiddenFile: true);
        await using (var stream = temporary.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                LibraryDirectoryOwnershipMarker.SerializePayload(target));
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        if (existing != null)
        {
            temporary.ReplaceWithinParent(fileName, existing);
        }
        else
        {
            temporary.MoveWithinParent(fileName);
        }
    }

    internal static async Task UpgradeLegacyAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        CancellationToken cancellationToken)
    {
        await UpgradeLegacyMarkerAsync(
            ownership,
            directory,
            LibraryDirectoryOwnershipMarker.FileName,
            cancellationToken);
        await UpgradeLegacyMarkerAsync(
            ownership,
            parent,
            $".listenarr-directory-owner-{ownership.OwnershipToken}.json",
            cancellationToken);
    }

    private static async Task UpgradeLegacyMarkerAsync(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        CancellationToken cancellationToken)
    {
        var temporaryName = fileName + ".v2.tmp";
        using var predecessor = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false)
            ?? throw new InvalidOperationException(
                "The ownership marker predecessor is missing.");
        var predecessorPayload = LibraryDirectoryOwnershipMarker.ReadPayload(predecessor);
        using var existingTemporary = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: true);
        if (existingTemporary != null)
        {
            var temporaryPayload =
                LibraryDirectoryOwnershipMarker.ReadPayload(existingTemporary);
            if (!LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    ownership,
                    temporaryPayload))
            {
                throw new InvalidOperationException(
                    "The ownership marker temporary file is stale or mismatched.");
            }

            if (LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                    ownership,
                    predecessorPayload))
            {
                existingTemporary.Delete();
                return;
            }
            if (!LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(
                    ownership,
                    predecessorPayload))
            {
                throw new InvalidOperationException(
                    "The ownership marker predecessor is not the expected legacy marker.");
            }

            existingTemporary.ReplaceWithinParent(fileName, predecessor);
            return;
        }

        if (LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                ownership,
                predecessorPayload))
        {
            return;
        }
        if (!LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(
                ownership,
                predecessorPayload))
        {
            throw new InvalidOperationException(
                "The ownership marker predecessor is not upgradeable.");
        }

        using var temporary = parent.CreateNewFile(temporaryName, hiddenFile: true);
        await using (var stream = temporary.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                LibraryDirectoryOwnershipMarker.SerializePayload(ownership));
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        temporary.ReplaceWithinParent(fileName, predecessor);
    }

    private static void ValidateExistingMarker(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName)
    {
        using var marker = parent.OpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        LibraryDirectoryOwnershipMarker.ValidateMarkerFile(ownership, marker);
        if (!parent.VisiblePathMatches() || !marker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The durable ownership marker changed during pinned validation.");
        }
    }
}
