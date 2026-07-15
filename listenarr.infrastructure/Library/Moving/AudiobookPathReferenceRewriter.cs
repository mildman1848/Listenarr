using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed class AudiobookPathRewriteException(string message)
    : InvalidOperationException(message);

internal static class AudiobookPathReferenceRewriter
{
    public static void Rewrite(
        Audiobook audiobook,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBasePath);

        EnsureCurrentBasePathMatchesExpectedState(
            audiobook.BasePath,
            sourceBasePath,
            targetBasePath,
            sourceSemantics,
            targetSemantics);

        var filePath = audiobook.FilePath;
        var imageUrl = audiobook.ImageUrl;
        var rewrittenFiles = new List<(AudiobookFile File, string? Path)>();

        if (!string.IsNullOrWhiteSpace(sourceBasePath))
        {
            filePath = RewriteAbsoluteReference(
                audiobook.FilePath,
                sourceBasePath,
                targetBasePath,
                sourceSemantics,
                targetSemantics);
            imageUrl = RewriteAbsoluteReference(
                audiobook.ImageUrl,
                sourceBasePath,
                targetBasePath,
                sourceSemantics,
                targetSemantics);

            foreach (var file in audiobook.Files ?? [])
            {
                rewrittenFiles.Add((file, RewriteAbsoluteReference(
                    file.Path,
                    sourceBasePath,
                    targetBasePath,
                    sourceSemantics,
                    targetSemantics)));
            }
        }

        // Apply rewritten references only after every path has been validated so
        // one bad stored value cannot leave the audiobook half-rebased.
        audiobook.FilePath = filePath;
        audiobook.ImageUrl = imageUrl;
        foreach (var (file, path) in rewrittenFiles)
        {
            file.Path = path;
        }

        audiobook.BasePath = targetBasePath;
    }

    private static void EnsureCurrentBasePathMatchesExpectedState(
        string? currentBasePath,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (string.IsNullOrWhiteSpace(sourceBasePath))
        {
            if (string.IsNullOrWhiteSpace(currentBasePath)
                || StoredPathsMatch(currentBasePath, targetBasePath)
                || PathsMatch(currentBasePath, targetBasePath, targetSemantics))
            {
                return;
            }

            throw new AudiobookPathRewriteException(
                "The audiobook path changed before its path references could be rewritten.");
        }

        if (!string.IsNullOrWhiteSpace(currentBasePath)
            && (StoredPathsMatch(currentBasePath, sourceBasePath)
                || PathsMatch(currentBasePath, sourceBasePath, sourceSemantics)
                || StoredPathsMatch(currentBasePath, targetBasePath)
                || PathsMatch(currentBasePath, targetBasePath, targetSemantics)))
        {
            return;
        }

        throw new AudiobookPathRewriteException(
            "The audiobook path changed before its path references could be rewritten.");
    }

    private static bool StoredPathsMatch(string currentPath, string expectedPath) =>
        string.Equals(currentPath, expectedPath, StringComparison.Ordinal)
        || string.Equals(
            currentPath,
            FileUtils.NormalizeStoredPath(expectedPath),
            StringComparison.Ordinal);

    private static bool PathsMatch(
        string path,
        string expectedPath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, expectedPath, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? RewriteAbsoluteReference(
        string? path,
        string sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (string.IsNullOrWhiteSpace(path)
            || IsRemoteUri(path))
        {
            return path;
        }

        bool isInsideSource;
        try
        {
            isInsideSource = FileSystemPathIdentity.IsSameOrInside(path, sourceBasePath, sourceSemantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Relative and non-filesystem references are intentionally preserved.
            return path;
        }

        if (!isInsideSource)
        {
            return path;
        }

        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                sourceBasePath,
                path,
                sourceSemantics,
                out var relativePath))
        {
            throw new AudiobookPathRewriteException(
                $"Stored audiobook path '{path}' could not be mapped to the new base path.");
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            return targetBasePath;
        }

        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                targetBasePath,
                FileSystemPathIdentity.ConvertRelativePathSyntax(
                    relativePath,
                    sourceSemantics.Syntax,
                    targetSemantics.Syntax),
                targetSemantics,
                out var rewrittenPath))
        {
            throw new AudiobookPathRewriteException(
                $"Stored audiobook path '{path}' could not be mapped to the new base path.");
        }

        return rewrittenPath;
    }

    private static bool IsRemoteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile;
}
