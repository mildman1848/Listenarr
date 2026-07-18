using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal static class LibraryDirectoryOwnershipMarker
{
    internal const string FileName = ".listenarr-directory-owner.json";
    private const int Version = 1;
    private const long MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task EnsureAsync(
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken) =>
        EnsureAtDirectoryAsync(
            ownership,
            ownership.CanonicalPath,
            cancellationToken);

    public static async Task EnsureAtDirectoryAsync(
        LibraryDirectoryOwnership ownership,
        string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ValidateOwnershipToken(ownership.OwnershipToken);
        ValidateDirectory(directory);
        var payload = new MarkerPayload(
            Version,
            ownership.OwnershipToken,
            ownership.CanonicalPath);
        await EnsureMarkerFileAsync(
            GetInsidePath(directory),
            payload,
            cancellationToken);
        await EnsureMarkerFileAsync(
            GetSiblingPath(ownership),
            payload,
            cancellationToken);
        Validate(ownership, directory);
    }

    public static void Validate(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ValidateDirectory(directory);
        ValidateMarkerFile(ownership, GetInsidePath(directory));
        ValidateMarkerFile(ownership, GetSiblingPath(ownership));
    }

    public static bool ContainsOnlyInsideMarker(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        Validate(ownership, directory);
        var markerPath = GetInsidePath(directory);
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(2).ToList();
        return entries.Count == 1
            && string.Equals(entries[0], markerPath, StringComparison.Ordinal);
    }

    public static void DeleteInsideMarker(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        Validate(ownership, directory);
        File.Delete(GetInsidePath(directory));
    }

    public static void DeleteSiblingMarker(LibraryDirectoryOwnership ownership)
    {
        var markerPath = GetSiblingPath(ownership);
        ValidateMarkerFile(ownership, markerPath);
        File.Delete(markerPath);
    }

    public static bool TryDeleteRetiredSiblingMarker(
        LibraryDirectoryOwnership ownership,
        out string? reason)
    {
        var markerPath = GetSiblingPath(ownership);
        if (!File.Exists(markerPath))
        {
            reason = null;
            return true;
        }

        try
        {
            ValidateMarkerFile(ownership, markerPath);
            File.Delete(markerPath);
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException)
        {
            if (!File.Exists(markerPath))
            {
                reason = null;
                return true;
            }

            reason = exception.Message;
            return false;
        }
    }

    public static IReadOnlyList<string> GetMarkerPaths(
        LibraryDirectoryOwnership ownership) =>
        [GetInsidePath(ownership.CanonicalPath), GetSiblingPath(ownership)];

    public static bool HasValidSiblingMarker(LibraryDirectoryOwnership ownership)
    {
        try
        {
            ValidateMarkerFile(ownership, GetSiblingPath(ownership));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task EnsureMarkerFileAsync(
        string markerPath,
        MarkerPayload payload,
        CancellationToken cancellationToken)
    {
        if (File.Exists(markerPath))
        {
            ValidateMarkerFile(payload, markerPath);
            return;
        }

        var temporaryPath = markerPath + $".writing-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(payload, JsonOptions),
                cancellationToken);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, markerPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                File.Delete(temporaryPath);
            }

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(markerPath, File.GetAttributes(markerPath) | FileAttributes.Hidden);
            }
            ValidateMarkerFile(payload, markerPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateMarkerFile(
        LibraryDirectoryOwnership ownership,
        string markerPath) =>
        ValidateMarkerFile(
            new MarkerPayload(
                Version,
                ownership.OwnershipToken,
                ownership.CanonicalPath),
            markerPath,
            ownership.GetIdentity().Semantics);

    private static void ValidateMarkerFile(
        MarkerPayload expected,
        string markerPath,
        FileSystemPathSemantics? semantics = null)
    {
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker is missing.");
        }

        var markerInfo = new FileInfo(markerPath);
        if ((markerInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker is a symbolic link or reparse point.");
        }
        if (markerInfo.Length <= 0 || markerInfo.Length > MaximumBytes)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker has an invalid size.");
        }

        MarkerPayload? marker;
        try
        {
            marker = JsonSerializer.Deserialize<MarkerPayload>(
                File.ReadAllText(markerPath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker is invalid.",
                exception);
        }

        var pathsMatch = semantics.HasValue
            ? marker != null
                && FileSystemPathIdentity.AreEquivalent(
                    marker.CanonicalPath,
                    expected.CanonicalPath,
                    semantics.Value)
            : marker != null
                && string.Equals(
                    marker.CanonicalPath,
                    expected.CanonicalPath,
                    StringComparison.Ordinal);
        if (marker == null
            || marker.Version != Version
            || !string.Equals(marker.OwnershipToken, expected.OwnershipToken, StringComparison.Ordinal)
            || !pathsMatch)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker does not match the persisted ownership claim.");
        }
    }

    private static string GetInsidePath(string directory) => Path.Join(directory, FileName);

    internal static void ValidateOwnershipToken(string ownershipToken)
    {
        if (!Guid.TryParseExact(ownershipToken, "N", out _))
        {
            throw new InvalidOperationException(
                "The durable directory ownership token is invalid.");
        }
    }

    private static string GetSiblingPath(LibraryDirectoryOwnership ownership)
    {
        ValidateOwnershipToken(ownership.OwnershipToken);
        var parent = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        return Path.Join(
            parent,
            $".listenarr-directory-owner-{ownership.OwnershipToken}.json");
    }

    private static void ValidateDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "The durable directory ownership path does not exist.");
        }

        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The durable directory ownership path is a symbolic link or reparse point.");
        }
    }

    private sealed record MarkerPayload(
        int Version,
        string OwnershipToken,
        string CanonicalPath);
}
