namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void RejectTargetNavigationSegments(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var root = Path.GetPathRoot(targetPath);
        var relativePath = string.IsNullOrEmpty(root)
            ? targetPath
            : targetPath[root.Length..];
        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain current directory segments.",
                nameof(targetPath));
        }

        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain parent traversal segments.",
                nameof(targetPath));
        }
    }

    private static void ApplyRootDirectoryObjectIdentity(
        RootFolder root,
        DirectoryObjectIdentityResolution identity)
    {
        root.DirectoryObjectIdentityVersion = identity.Version;
        root.DirectoryObjectIdentity = identity.Value;
        root.DirectoryObjectIdentityUnavailableReason = identity.UnavailableReason;
    }

    private async Task<DirectoryObjectIdentityResolution>
        ResolveOrCreateRelocationTargetIdentityAsync(
            string targetPath,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                "The relocation target has no parent directory.");
        var childName = Path.GetFileName(targetPath);
        using var creation = PinnedDirectoryCreation.TryCreate(
            parentPath,
            childName);
        if (creation.Created)
        {
            using var anchor = creation.OpenCreatedDirectoryAnchor();
            if (!creation.VisiblePathMatches()
                || !anchor.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The relocation target changed while its physical identity was reserved.");
            }

            return new DirectoryObjectIdentityResolution(
                1,
                anchor.GetDirectoryObjectIdentity(),
                null);
        }

        try
        {
            using var existing = PinnedDirectoryCreation.OpenPinnedBoundary(
                targetPath);
            return new DirectoryObjectIdentityResolution(
                1,
                existing.GetDirectoryObjectIdentity(),
                null);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "The relocation target could not be reserved with a stable physical directory identity.",
                exception);
        }
    }

    private async Task<DirectoryObjectIdentityResolution>
        ResolveExistingDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken)
    {
        if (_directoryObjectIdentityResolver != null)
        {
            return await _directoryObjectIdentityResolver.ResolveAsync(
                path,
                cancellationToken);
        }

        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(path);
            return new DirectoryObjectIdentityResolution(
                1,
                anchor.GetDirectoryObjectIdentity(),
                null);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return DirectoryObjectIdentityResolution.Unavailable(
                exception.Message);
        }
    }
}
