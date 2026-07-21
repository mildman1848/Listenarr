using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> IsLinkedFilesystemAliasAsync(
        string source,
        string destination)
    {
        var sourcePath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(source));
        var destinationPath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(destination));
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)
            || !TryResolvePhysicalPath(sourcePath, out var sourceResolution)
            || !TryResolvePhysicalPath(destinationPath, out var destinationResolution)
            || sourceResolution.EntryKind != PhysicalPathEntryKind.File
            || destinationResolution.EntryKind != PhysicalPathEntryKind.File
            || (!sourceResolution.EncounteredLink
                && !destinationResolution.EncounteredLink))
        {
            return false;
        }

        if (string.Equals(
                sourceResolution.ResolvedPath,
                destinationResolution.ResolvedPath,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (_semanticsResolver == null)
        {
            return false;
        }

        try
        {
            var resolution = await _semanticsResolver.ResolveAsync(sourcePath);
            return resolution.State == PathIdentityState.Valid
                && FileSystemPathIdentity.AreEquivalent(
                    sourceResolution.ResolvedPath,
                    destinationResolution.ResolvedPath,
                    resolution.Semantics);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
