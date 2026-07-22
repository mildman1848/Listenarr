from pathlib import Path

path = Path("listenarr.infrastructure/FileSystem/FileMover.cs")
text = path.read_text(encoding="utf-8")

anchor = '''            // Fallback to copy plus verified, non-recursive source cleanup. New or
            // changed source content is preserved instead of being recursively deleted.
            try
            {
                await CopyDirectorySnapshotAsync(copySnapshot, destDir);
                var cleanup = await CleanupCopiedSourceTreeAsync(sourceDir, destDir);
'''
replacement = '''            var destinationRoot = Path.GetFullPath(destDir);
            var destinationPreparation = await PrepareMoveDestinationAsync(
                copySnapshot,
                destinationRoot);
            if (!destinationPreparation.Success)
            {
                _logger.LogWarning(
                    "Blocked copy-and-delete directory fallback because the destination could not be prepared safely: {Reason}",
                    destinationPreparation.Reason);
                return false;
            }

            using var emptyDestinationPlaceholder = destinationPreparation.Placeholder;
            var destinationPublished = false;

            // Fallback to copy plus verified, non-recursive source cleanup. New or
            // changed source content is preserved instead of being recursively deleted.
            try
            {
                await CopyDirectorySnapshotAsync(copySnapshot, destinationRoot);
                destinationPublished = true;
                if (emptyDestinationPlaceholder != null
                    && !emptyDestinationPlaceholder.TryDeleteAfterPublication(
                        out var placeholderCleanupReason))
                {
                    _logger.LogWarning(
                        "Directory copy published safely, but the empty destination placeholder could not be removed: {Reason}",
                        placeholderCleanupReason);
                    return false;
                }

                var cleanup = await CleanupCopiedSourceTreeAsync(sourceDir, destinationRoot);
'''
if text.count(anchor) != 1:
    raise RuntimeError(f"Expected one fallback integration anchor, found {text.count(anchor)}")
text = text.replace(anchor, replacement, 1)

text = text.replace(
    '''                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _options.EnableRobocopy && _processRunner != null)
                    {
''',
    '''                    var robocopyFallbackSafe = false;
                    if (emptyDestinationPlaceholder == null
                        && !Directory.Exists(destinationRoot)
                        && await SourceSnapshotStillMatchesAsync(copySnapshot))
                    {
                        try
                        {
                            await EnsureDirectoryCopyTargetSafeAsync(
                                copySnapshot.SourceRoot,
                                destinationRoot,
                                destinationRoot);
                            robocopyFallbackSafe = true;
                        }
                        catch (Exception safetyException) when (safetyException is not (
                            OperationCanceledException or OutOfMemoryException or StackOverflowException))
                        {
                            _logger.LogWarning(
                                safetyException,
                                "Robocopy fallback was blocked because directory safety could not be revalidated");
                        }
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && _options.EnableRobocopy
                        && _processRunner != null
                        && robocopyFallbackSafe)
                    {
''',
    1,
)
if "var robocopyFallbackSafe = false;" not in text:
    raise RuntimeError("Robocopy safety replacement did not apply")

closing_anchor = '''
                return false;
            }
        }

        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
'''
closing_replacement = '''
                return false;
            }
            finally
            {
                if (!destinationPublished
                    && emptyDestinationPlaceholder != null
                    && !emptyDestinationPlaceholder.TryRestore(out var restoreReason))
                {
                    _logger.LogError(
                        "The empty destination placeholder could not be restored after directory copy failure: {Reason}",
                        restoreReason);
                }
            }
        }

        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)
'''
if text.count(closing_anchor) != 1:
    raise RuntimeError(f"Expected one MoveDirectory closing anchor, found {text.count(closing_anchor)}")
path.write_text(text.replace(closing_anchor, closing_replacement, 1), encoding="utf-8")
