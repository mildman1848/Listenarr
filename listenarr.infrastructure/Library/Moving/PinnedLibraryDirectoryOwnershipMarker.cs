using System.Text.Json;

namespace Listenarr.Infrastructure.Library.Moving;

internal static class PinnedLibraryDirectoryOwnershipMarker
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        var payload = JsonSerializer.Serialize(
            new MarkerPayload(
                Version,
                ownership.OwnershipToken,
                ownership.CanonicalPath),
            JsonOptions);
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
        var markerPath = Path.Join(parent.FullPath, fileName);
        if (File.Exists(markerPath))
        {
            ValidateExistingMarker(ownership, parent, fileName);
            return;
        }

        var temporaryName = fileName + $".writing-{Guid.NewGuid():N}";
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
            && File.Exists(markerPath))
        {
            ValidateExistingMarker(ownership, parent, fileName);
            return;
        }

        ValidateExistingMarker(ownership, parent, fileName);
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

    private sealed record MarkerPayload(
        int Version,
        string OwnershipToken,
        string CanonicalPath);
}
