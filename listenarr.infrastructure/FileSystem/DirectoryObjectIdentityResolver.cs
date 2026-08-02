using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class DirectoryObjectIdentityResolver(
    Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, string>?
        nativeIdentityResolver = null) : IDirectoryObjectIdentityResolver
{
    private readonly Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, string>
        _nativeIdentityResolver = nativeIdentityResolver
        ?? (static anchor => anchor.GetDirectoryObjectIdentity());

    public Task<DirectoryObjectIdentityResolution> ResolveAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(
            path,
            enrollIfMissing: true,
            expectedLegacyIdentity: null,
            cancellationToken);

    public Task<DirectoryObjectIdentityResolution> ResolveExistingAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(
            path,
            enrollIfMissing: false,
            expectedLegacyIdentity: null,
            cancellationToken);

    public Task<DirectoryObjectIdentityResolution> UpgradeLegacyAsync(
        string path,
        int legacyVersion,
        string legacyValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyValue);
        if (legacyVersion != 1)
        {
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(
                    $"Directory identity version {legacyVersion} cannot be upgraded automatically."));
        }

        return ResolveCoreAsync(
            path,
            enrollIfMissing: true,
            expectedLegacyIdentity: legacyValue,
            cancellationToken);
    }

    private async Task<DirectoryObjectIdentityResolution> ResolveCoreAsync(
        string path,
        bool enrollIfMissing,
        string? expectedLegacyIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var pathReason))
        {
            return DirectoryObjectIdentityResolution.Unavailable(pathReason);
        }

        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalPath);
            var nativeIdentity = _nativeIdentityResolver(anchor);
            if (!anchor.VisiblePathMatches())
            {
                return DirectoryObjectIdentityResolution.Unavailable(
                    "The directory changed while its physical identity was captured.");
            }

            if (expectedLegacyIdentity != null
                && !string.Equals(
                    nativeIdentity,
                    expectedLegacyIdentity,
                    StringComparison.Ordinal))
            {
                return DirectoryObjectIdentityResolution.Unavailable(
                    "The live directory no longer matches its legacy physical identity and cannot be enrolled automatically.");
            }

            return await ManagedDirectoryEnrollment.ResolveAsync(
                anchor,
                nativeIdentity,
                enrollIfMissing,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException or InvalidOperationException)
        {
            return DirectoryObjectIdentityResolution.Unavailable(exception.Message);
        }
    }
}
