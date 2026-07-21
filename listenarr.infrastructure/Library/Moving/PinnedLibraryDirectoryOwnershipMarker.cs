using System.Text.Json;
using Listenarr.Infrastructure.FileSystem;

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
                "The newly created directory is no longer reachable through its validated pathname.");
        }

        var payload = JsonSerializer.Serialize(
            new MarkerPayload(
                Version,
                ownership.OwnershipToken,
                ownership.CanonicalPath),
            JsonOptions);
        await creation.WriteInsideFileAsync(
            LibraryDirectoryOwnershipMarker.FileName,
            payload,
            cancellationToken);
        await creation.WriteParentFileAsync(
            $".listenarr-directory-owner-{ownership.OwnershipToken}.json",
            payload,
            cancellationToken);

        if (!creation.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The newly created directory pathname changed during ownership publication.");
        }
    }

    private sealed record MarkerPayload(
        int Version,
        string OwnershipToken,
        string CanonicalPath);
}
