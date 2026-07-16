using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private async Task<FileSystemPathSemantics> ResolveDestinationSemanticsAsync(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException("Destination base path is unavailable.");
        }

        RootFolder? bestRoot = null;
        var bestRootLength = -1;
        foreach (var root in await _rootFolderService.GetAllAsync())
        {
            if (string.IsNullOrWhiteSpace(root.Path))
            {
                continue;
            }

            var rootResolution = await _semanticsResolver.ResolveAsync(
                root.Path,
                root.CaseSensitivityMode);
            if (rootResolution.State != PathIdentityState.Valid
                || !FileSystemPathIdentity.IsSameOrInside(
                    basePath,
                    root.Path,
                    rootResolution.Semantics))
            {
                continue;
            }

            var canonicalRoot = FileSystemPathIdentity.Canonicalize(
                root.Path,
                rootResolution.Semantics.Syntax);
            if (canonicalRoot.Length > bestRootLength)
            {
                bestRoot = root;
                bestRootLength = canonicalRoot.Length;
            }
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            basePath,
            bestRoot?.CaseSensitivityMode ?? FileSystemCaseSensitivityMode.Auto);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason ?? "Destination filesystem identity is unavailable.");
        }

        return resolution.Semantics;
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        string defaultReason)
    {
        var resolution = await _semanticsResolver.ResolveAsync(path);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(resolution.Reason ?? defaultReason);
        }

        return resolution.Semantics;
    }

    private async Task<bool> IsInsideAnyConfiguredRootAsync(
        string path,
        IEnumerable<RootFolder> rootFolders)
    {
        foreach (var rootFolder in rootFolders)
        {
            if (string.IsNullOrWhiteSpace(rootFolder.Path))
            {
                continue;
            }

            var resolution = await _semanticsResolver.ResolveAsync(
                rootFolder.Path,
                rootFolder.CaseSensitivityMode);
            if (resolution.State == PathIdentityState.Valid
                && FileSystemPathIdentity.IsSameOrInside(
                    path,
                    rootFolder.Path,
                    resolution.Semantics))
            {
                return true;
            }
        }

        return false;
    }
}
