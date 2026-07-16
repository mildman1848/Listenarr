using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<FileSystemSemanticsResolution> ResolveDestinationResolutionAsync(
        string basePath,
        CancellationToken cancellationToken)
    {
        var mode = await ResolveDestinationCaseSensitivityModeAsync(
            basePath,
            cancellationToken);
        var resolution = await semanticsResolver.ResolveAsync(
            basePath,
            mode,
            cancellationToken);
        return resolution.State == PathIdentityState.Valid
            ? resolution
            : throw new InvalidOperationException(
                resolution.Reason ?? "Destination filesystem identity is unavailable.");
    }

    private async Task<FileSystemCaseSensitivityMode> ResolveDestinationCaseSensitivityModeAsync(
        string basePath,
        CancellationToken cancellationToken)
    {
        RootFolder? bestRoot = null;
        var bestRootLength = -1;
        foreach (var root in await rootFolderService.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                root.Path,
                root.CaseSensitivityMode,
                cancellationToken);
            if (resolution.State != PathIdentityState.Valid
                || !FileSystemPathIdentity.IsSameOrInside(
                    basePath,
                    root.Path,
                    resolution.Semantics))
            {
                continue;
            }

            var canonicalRoot = FileSystemPathIdentity.Canonicalize(
                root.Path,
                resolution.Semantics.Syntax);
            if (canonicalRoot.Length > bestRootLength)
            {
                bestRoot = root;
                bestRootLength = canonicalRoot.Length;
            }
        }

        return bestRoot?.CaseSensitivityMode ?? FileSystemCaseSensitivityMode.Auto;
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var resolution = await semanticsResolver.ResolveAsync(
            path,
            cancellationToken: cancellationToken);
        return resolution.State == PathIdentityState.Valid
            ? resolution.Semantics
            : throw new InvalidOperationException(resolution.Reason ?? defaultReason);
    }

    private static string NormalizeAuthoritativeBasePath(
        string basePath,
        FileSystemSemanticsResolution resolution)
    {
        return string.IsNullOrWhiteSpace(resolution.CanonicalPath)
            ? FileSystemPathIdentity.Canonicalize(basePath, resolution.Semantics.Syntax)
            : FileSystemPathIdentity.Canonicalize(
                resolution.CanonicalPath,
                resolution.Semantics.Syntax);
    }
}
