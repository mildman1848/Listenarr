using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public sealed class FileSystemSemanticsResolver : IFileSystemSemanticsResolver
{
    private const string ProbePrefix = ".listenarr-case-probe-";

    internal Action<string>? BeforeProbeForTest { get; init; }
    internal Action<string, string>? AfterPrimaryProbeCreatedForTest { get; init; }

    public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        string path,
        FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Filesystem semantics require an absolute path.", nameof(path));
        }

        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        var fullPath = FileUtils.NormalizeStoredPath(path);

        if (mode != FileSystemCaseSensitivityMode.Auto)
        {
            var explicitSensitivity = mode == FileSystemCaseSensitivityMode.Sensitive
                ? FileSystemCaseSensitivity.Sensitive
                : FileSystemCaseSensitivity.Insensitive;
            return ValueTask.FromResult(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(syntax, explicitSensitivity),
                PathIdentityState.Valid,
                FindExistingBoundary(fullPath) ?? Path.GetPathRoot(fullPath) ?? fullPath,
                CanonicalPath: fullPath));
        }

        var boundary = FindExistingBoundary(fullPath);
        if (boundary == null)
        {
            return ValueTask.FromResult(Unavailable(syntax, fullPath, "No existing filesystem boundary could be found."));
        }

        BeforeProbeForTest?.Invoke(boundary);
        var resolved = Probe(boundary, syntax);
        return ValueTask.FromResult(resolved with { CanonicalPath = fullPath });
    }

    private FileSystemSemanticsResolution Probe(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        var probeName = ProbePrefix + Guid.NewGuid().ToString("N") + "-a";
        var alternateName = probeName.ToUpperInvariant();
        PinnedDirectoryCreation.PinnedFileEntry? primary = null;
        PinnedDirectoryCreation.PinnedFileEntry? alternate = null;
        var alternateCreated = false;
        try
        {
            using var pinnedBoundary =
                PinnedDirectoryCreation.OpenPinnedBoundary(boundary);
            primary = pinnedBoundary.CreateNewFile(probeName);
            AfterPrimaryProbeCreatedForTest?.Invoke(
                primary.FullPath,
                Path.Join(boundary, alternateName));

            try
            {
                alternate = pinnedBoundary.CreateNewFile(alternateName);
                alternateCreated = true;
                if (!pinnedBoundary.VisiblePathMatches()
                    || !primary.VisiblePathMatches()
                    || !alternate.VisiblePathMatches())
                {
                    return Unavailable(
                        syntax,
                        boundary,
                        "Filesystem case-sensitivity probe entries changed during classification.");
                }

                return new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        syntax,
                        FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    boundary);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception
                {
                    NativeErrorCode: 17
                })
            {
                alternate = pinnedBoundary.TryOpenExistingFile(
                    alternateName,
                    requireDeleteAccess: false);
                if (alternate == null
                    || !pinnedBoundary.VisiblePathMatches()
                    || !primary.VisiblePathMatches()
                    || !alternate.VisiblePathMatches()
                    || !primary.IdentifiesSameEntry(alternate)
                    || primary.GetLinkCount() != 1
                    || !pinnedBoundary.VisiblePathMatches()
                    || !primary.VisiblePathMatches()
                    || !alternate.VisiblePathMatches())
                {
                    return Unavailable(
                        syntax,
                        boundary,
                        "Filesystem case-sensitivity probe collision could not be attributed to the created entry.");
                }

                return new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        syntax,
                        FileSystemCaseSensitivity.Insensitive),
                    PathIdentityState.Valid,
                    boundary);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return Unavailable(
                syntax,
                boundary,
                $"Filesystem case sensitivity could not be probed: {exception.GetType().Name}.");
        }
        finally
        {
            if (alternateCreated && alternate != null)
            {
                TryDeleteProbe(alternate);
            }
            alternate?.Dispose();
            if (primary != null)
            {
                TryDeleteProbe(primary);
            }
            primary?.Dispose();
        }
    }

    private static string? FindExistingBoundary(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static FileSystemSemanticsResolution Unavailable(
        FileSystemPathSyntax syntax,
        string boundary,
        string reason)
    {
        return new FileSystemSemanticsResolution(
            new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Unknown),
            PathIdentityState.Unavailable,
            boundary,
            reason,
            boundary);
    }

    private static void TryDeleteProbe(
        PinnedDirectoryCreation.PinnedFileEntry probe)
    {
        try
        {
            if (probe.VisiblePathMatches())
            {
                probe.Delete(immediateWindows: true);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Failed to remove filesystem case-sensitivity probe {0}: {1}",
                probe.FullPath,
                exception.Message);
        }
    }
}
