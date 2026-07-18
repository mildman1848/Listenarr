using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private async Task<IReadOnlyList<AudiobookScanRemovedFile>> ReconcileMissingFilesAsync(
        AudiobookScanCommand command,
        Audiobook audiobook,
        IReadOnlyCollection<AudiobookFile> existingFiles,
        IReadOnlyDictionary<int, string> resolvedPaths,
        ScanDiscoveryResult discovery,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!command.AllowReconciliation || !command.IsAuthoritativeScope)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "ReconciliationNotAuthorized",
                command.ScanRoot,
                "This scan scope is not authorized to remove tracked file rows."));
            return [];
        }

        if (!discovery.CanReconcile)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "ReconciliationSkippedIncompleteScan",
                command.ScanRoot,
                "Filesystem discovery was incomplete; destructive reconciliation was skipped."));
            await historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "Scan Incomplete",
                Message = "Scan incomplete; tracked file reconciliation was skipped.",
                Source = command.Source,
                CorrelationId = command.CorrelationId,
                Data = JsonSerializer.Serialize(new
                {
                    command.ScanRoot,
                    Issues = discovery.Issues.Select(issue => new
                    {
                        Kind = issue.Kind.ToString(),
                        issue.Path,
                        issue.Message
                    })
                }),
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return [];
        }

        var removed = new List<AudiobookScanRemovedFile>();
        foreach (var file in existingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolvedPaths.TryGetValue(file.Id, out var resolvedPath))
            {
                continue;
            }

            if (!FileSystemPathIdentity.IsSameOrInside(
                    resolvedPath,
                    command.ScanRoot,
                    semantics))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "TrackedFileOutsideScope",
                    resolvedPath,
                    "The tracked file lies outside the authoritative scan scope and was preserved."));
                continue;
            }

            if (fileSystem.FileExists(resolvedPath))
            {
                if (!discovery.AttributedFiles.Contains(resolvedPath, semantics.Comparer))
                {
                    diagnostics.Add(new AudiobookScanDiagnostic(
                        "ExistingFileNotAttributed",
                        resolvedPath,
                        "The file still exists but attribution was inconclusive; its row was preserved."));
                }

                continue;
            }

            await fileRepository.DeleteAsync(file.Id, cancellationToken);
            removed.Add(new AudiobookScanRemovedFile(file.Id, file.Path));
            await historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "File Removed",
                Message = $"Verified missing file removed: {Path.GetFileName(file.Path)}",
                Source = command.Source,
                CorrelationId = command.CorrelationId,
                Data = JsonSerializer.Serialize(new
                {
                    StoredPath = file.Path,
                    ResolvedPath = resolvedPath,
                    file.Size,
                    file.Format,
                    file.Source
                }),
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
        }

        return removed;
    }

    private async Task<int> ReconcileLegacyFilePathAsync(
        AudiobookScanCommand command,
        Audiobook audiobook,
        ScanDiscoveryResult discovery,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            return 0;
        }

        var storedPath = audiobook.FilePath;
        if (!TryResolveLegacyPath(
                audiobook,
                storedPath,
                semantics,
                out var resolvedPath,
                out var reason))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyPathUnresolved",
                storedPath,
                reason ?? "The legacy file path could not be resolved."));
            return 0;
        }

        if (fileSystem.FileExists(resolvedPath))
        {
            if (!discovery.AttributedFiles.Contains(
                    resolvedPath,
                    semantics.Comparer))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "LegacyPathNotAttributed",
                    resolvedPath,
                    "The legacy path still exists but was not attributed to this audiobook; it was preserved without being claimed."));
                return 0;
            }

            return await fileService.EnsureAudiobookFileAsync(
                audiobook,
                resolvedPath,
                command.Source + "-legacy",
                cancellationToken)
                ? 1
                : 0;
        }

        if (!command.AllowReconciliation
            || !command.IsAuthoritativeScope
            || !discovery.CanReconcile
            || !FileSystemPathIdentity.IsSameOrInside(
                resolvedPath,
                command.ScanRoot,
                semantics))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyMissingPathPreserved",
                resolvedPath,
                "The missing legacy file path was outside proven reconciliation authority."));
            return 0;
        }

        audiobook.FilePath = null;
        audiobook.FileSize = null;
        await audiobookRepository.UpdateAsync(audiobook);
        await historyRepository.AddAsync(new History
        {
            AudiobookId = audiobook.Id,
            AudiobookTitle = audiobook.Title ?? "Unknown",
            EventType = "File Removed",
            Message = "Verified missing legacy file path cleared.",
            Source = command.Source,
            CorrelationId = command.CorrelationId,
            Data = JsonSerializer.Serialize(new
            {
                StoredPath = storedPath,
                ResolvedPath = resolvedPath,
                Source = "legacy-reconciliation"
            }),
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
        return 0;
    }

    private static bool TryResolveLegacyPath(
        Audiobook audiobook,
        string storedPath,
        FileSystemPathSemantics semantics,
        out string resolvedPath,
        out string? reason)
    {
        resolvedPath = string.Empty;
        reason = null;
        try
        {
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    storedPath,
                    semantics.Syntax,
                    out _))
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    storedPath,
                    semantics.Syntax);
                return true;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
            {
                reason = "The legacy path uses an unexpected filesystem syntax.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath,
                    storedPath,
                    semantics,
                    out resolvedPath))
            {
                reason = "The relative legacy path cannot be resolved inside BasePath.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            reason = exception.Message;
            return false;
        }
    }
}
