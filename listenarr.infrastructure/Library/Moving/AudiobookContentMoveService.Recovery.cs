/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const int RecoveryMarkerVersion = 1;
    private const string CopyStartedStage = "copy-started";
    private const string CopyCompletedStage = "copy-complete";
    private const string AtomicRenameCompletedStage = "atomic-rename-complete";
    private const string SourceCleanupCompletedStage = "source-cleanup-complete";

    private sealed record MoveRecoveryMarker(
        int Version,
        Guid JobId,
        string Source,
        string Target,
        string Stage);

    private sealed record ParsedRecoveryMarker(
        MoveRecoveryMarker? StructuredMarker,
        string? ObsoleteStage)
    {
        public string Stage => StructuredMarker?.Stage
            ?? ObsoleteStage
            ?? throw new InvalidOperationException("The recovery marker has no stage.");

        public bool IsObsolete => ObsoleteStage != null;
    }

    private async Task RecoverRecoveryMarkerWriteFilesAsync(
        string markerDirectory,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(markerDirectory))
        {
            return;
        }

        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker cleanup directory");
        var authoritativeMarkerPath = GetRecoveryMarkerPath(markerDirectory, request.JobId);
        var writeFilePrefix = Path.GetFileName(authoritativeMarkerPath) + ".writing-";
        foreach (var writePath in Directory.EnumerateFiles(
                markerDirectory,
                writeFilePrefix + "*",
                SearchOption.TopDirectoryOnly)
            .ToList())
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    writePath,
                    [markerDirectory],
                    out var safeWritePath,
                    out var writeReason))
            {
                throw new MoveNeedsAttentionException(writeReason);
            }

            if ((File.GetAttributes(safeWritePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new MoveNeedsAttentionException(
                    "A recovery-marker write-temporary file is a symbolic link or reparse point.");
            }

            if (!TryParseMarkerWriteIdentity(
                    safeWritePath,
                    authoritativeMarkerPath,
                    out var writeIdentity)
                || writeIdentity.JobId != request.JobId)
            {
                throw new MoveNeedsAttentionException(
                    "A recovery-marker write-temporary filename does not match the active move job.");
            }

            var markerRead = ReadRecoveryMarkerWriteResult(safeWritePath);
            if (markerRead.State == MarkerReadState.TemporarilyUnreadable)
            {
                throw new IOException(
                    "A recovery-marker write file is temporarily unreadable and was preserved.",
                    markerRead.Error);
            }
            if (markerRead.State == MarkerReadState.Unsupported)
            {
                throw new MoveNeedsAttentionException(
                    "A recovery-marker write file uses an unsupported marker version or stage and was preserved.");
            }
            if (markerRead.State == MarkerReadState.CorruptOrTruncated)
            {
                if (writeIdentity.LeaseGeneration >= request.LeaseGeneration)
                {
                    throw new MoveNeedsAttentionException(
                        "A current or future-generation recovery-marker write file is truncated and was preserved.");
                }

                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                ValidateRecoveryMarkerWritePath(safeWritePath, markerDirectory);
                var currentRead = ReadRecoveryMarkerWriteResult(safeWritePath);
                if (currentRead.State == MarkerReadState.TemporarilyUnreadable)
                {
                    throw new IOException(
                        "A predecessor recovery-marker write file became temporarily unreadable and was preserved.",
                        currentRead.Error);
                }
                if (currentRead.State != MarkerReadState.CorruptOrTruncated
                    || !TryParseMarkerWriteIdentity(
                        safeWritePath,
                        authoritativeMarkerPath,
                        out var currentIdentity)
                    || currentIdentity != writeIdentity)
                {
                    throw new MoveNeedsAttentionException(
                        "A truncated recovery-marker write file changed before cleanup.");
                }

                File.Delete(safeWritePath);
                logger.LogInformation(
                    "Removed truncated predecessor recovery-marker write file for move job {JobId}",
                    request.JobId);
                continue;
            }

            var marker = markerRead.Marker
                ?? throw new MoveNeedsAttentionException("The recovery-marker write file is missing.");

            if (writeIdentity.LeaseGeneration > request.LeaseGeneration)
            {
                throw new MoveNeedsAttentionException(
                    "A future-generation recovery-marker write file was preserved.");
            }

            ValidateRecoveryMarker(
                new ParsedRecoveryMarker(marker, ObsoleteStage: null),
                request,
                source,
                target);
            var authoritativeMarker = ReadRecoveryMarker(authoritativeMarkerPath);
            ValidateRecoveryMarker(authoritativeMarker, request, source, target);
            if (authoritativeMarker == null)
            {
                await WriteRecoveryMarkerAsync(
                    markerDirectory,
                    request,
                    source,
                    target,
                    marker.Stage,
                    cancellationToken);
            }
            else if (!string.Equals(
                authoritativeMarker.Stage,
                marker.Stage,
                StringComparison.Ordinal))
            {
                if (CanAdvanceRecoveryStage(authoritativeMarker.Stage, marker.Stage))
                {
                    await WriteRecoveryMarkerAsync(
                        markerDirectory,
                        request,
                        source,
                        target,
                        marker.Stage,
                        cancellationToken);
                }
                else if (!CanAdvanceRecoveryStage(marker.Stage, authoritativeMarker.Stage))
                {
                    throw new MoveNeedsAttentionException(
                        "A recovery-marker write file belongs to an incompatible recovery workflow.");
                }
            }

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            ValidateRecoveryMarkerWritePath(safeWritePath, markerDirectory);
            var currentReadAfterAuthorization = ReadRecoveryMarkerWriteResult(safeWritePath);
            if (currentReadAfterAuthorization.State == MarkerReadState.TemporarilyUnreadable)
            {
                throw new IOException(
                    "A recovery-marker write file became temporarily unreadable and was preserved.",
                    currentReadAfterAuthorization.Error);
            }
            var currentMarker = currentReadAfterAuthorization.State == MarkerReadState.Valid
                ? currentReadAfterAuthorization.Marker!
                : throw new MoveNeedsAttentionException(
                    "A recovery-marker write-temporary file changed before deletion.");
            ValidateRecoveryMarker(
                new ParsedRecoveryMarker(currentMarker, ObsoleteStage: null),
                request,
                source,
                target);
            File.Delete(safeWritePath);
            logger.LogInformation(
                "Removed validated orphan recovery-marker write file for move job {JobId}",
                request.JobId);
        }
    }

    private static MarkerReadResult<MoveRecoveryMarker> ReadRecoveryMarkerWriteResult(
        string writePath)
    {
        var result = ReadJsonMarker<MoveRecoveryMarker>(writePath);
        if (result.State == MarkerReadState.Valid
            && (result.Marker!.Version != RecoveryMarkerVersion
                || !IsKnownRecoveryStage(result.Marker.Stage)))
        {
            return new MarkerReadResult<MoveRecoveryMarker>(
                MarkerReadState.Unsupported,
                result.Marker);
        }

        return result;
    }
}
