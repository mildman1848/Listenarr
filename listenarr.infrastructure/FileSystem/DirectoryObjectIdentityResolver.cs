using System.ComponentModel;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class DirectoryObjectIdentityResolver : IDirectoryObjectIdentityResolver
{
    public Task<DirectoryObjectIdentityResolution> ResolveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(path);
            var value = anchor.GetDirectoryObjectIdentity();
            if (!anchor.VisiblePathMatches())
            {
                return Task.FromResult(
                    DirectoryObjectIdentityResolution.Unavailable(
                        "The directory changed while its physical identity was captured."));
            }

            return Task.FromResult(new DirectoryObjectIdentityResolution(1, value, null));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException or InvalidOperationException)
        {
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(exception.Message));
        }
    }
}
