using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal static partial class ScanFileDiscovery
{
    private static EnumerationResult CollectCandidates(
        IFileSystem fileSystem,
        string scanRoot,
        Guid jobId,
        ILogger logger,
        FileSystemPathSemantics semantics)
    {
        var candidates = new HashSet<string>(semantics.Comparer);
        var enumeratedDirectories = new HashSet<string>(semantics.Comparer);
        var issues = new List<ScanDiscoveryIssue>();
        var directories = new Stack<string>();
        directories.Push(scanRoot);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            string normalizedDirectory;
            try
            {
                normalizedDirectory = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                RecordEnumerationFailure(
                    issues,
                    logger,
                    jobId,
                    directory,
                    exception);
                continue;
            }

            try
            {
                foreach (var file in fileSystem.EnumerateFiles(normalizedDirectory).ToList())
                {
                    try
                    {
                        if (fileSystem.IsReparsePoint(file))
                        {
                            issues.Add(new ScanDiscoveryIssue(
                                ScanDiscoveryIssueKind.LinkSkipped,
                                file,
                                "Linked files are not scanned."));
                            continue;
                        }

                        if (FileUtils.IsAudioFile(file))
                        {
                            candidates.Add(FileSystemPathIdentity.Canonicalize(
                                Path.GetFullPath(file),
                                semantics.Syntax));
                        }
                    }
                    catch (Exception exception) when (IsFilesystemException(exception))
                    {
                        RecordEnumerationFailure(
                            issues,
                            logger,
                            jobId,
                            file,
                            exception);
                    }
                }

                foreach (var child in fileSystem.EnumerateDirectories(normalizedDirectory).ToList())
                {
                    try
                    {
                        if (fileSystem.IsReparsePoint(child))
                        {
                            logger.LogWarning(
                                "Skipped linked directory while scanning job {JobId}: {Dir}",
                                jobId,
                                child);
                            issues.Add(new ScanDiscoveryIssue(
                                ScanDiscoveryIssueKind.LinkSkipped,
                                child,
                                "Linked directories are not traversed."));
                            continue;
                        }

                        directories.Push(child);
                    }
                    catch (Exception exception) when (IsFilesystemException(exception))
                    {
                        RecordEnumerationFailure(
                            issues,
                            logger,
                            jobId,
                            child,
                            exception);
                    }
                }

                enumeratedDirectories.Add(FileSystemPathIdentity.Canonicalize(
                    normalizedDirectory,
                    semantics.Syntax));
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                RecordEnumerationFailure(
                    issues,
                    logger,
                    jobId,
                    normalizedDirectory,
                    exception);
            }
        }

        return new EnumerationResult(
            candidates.OrderBy(path => path, semantics.Comparer).ToList(),
            enumeratedDirectories.OrderBy(path => path, semantics.Comparer).ToList(),
            issues);
    }

    private static bool IsFilesystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void RecordEnumerationFailure(
        ICollection<ScanDiscoveryIssue> issues,
        ILogger logger,
        Guid jobId,
        string path,
        Exception exception)
    {
        logger.LogWarning(
            exception,
            "Failed to enumerate path for scan job {JobId}: {Path}",
            jobId,
            path);
        issues.Add(new ScanDiscoveryIssue(
            ScanDiscoveryIssueKind.EnumerationFailure,
            path,
            exception.Message));
    }

    private sealed record EnumerationResult(
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> EnumeratedDirectories,
        IReadOnlyList<ScanDiscoveryIssue> Issues);
}
