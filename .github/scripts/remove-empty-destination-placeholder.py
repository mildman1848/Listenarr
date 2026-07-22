from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one anchor in {path}, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


file_mover = Path("listenarr.infrastructure/FileSystem/FileMover.cs")
replace_once(
    file_mover,
    '''            var destinationRoot = Path.GetFullPath(destDir);
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

''',
    '''            var destinationRoot = Path.GetFullPath(destDir);

''',
)
replace_once(
    file_mover,
    '''                await CopyDirectorySnapshotAsync(copySnapshot, destinationRoot);
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

''',
    '''                await CopyDirectorySnapshotAsync(copySnapshot, destinationRoot);

''',
)
replace_once(
    file_mover,
    '''                    if (emptyDestinationPlaceholder == null
                        && !Directory.Exists(destinationRoot)
                        && await SourceSnapshotStillMatchesAsync(copySnapshot))
''',
    '''                    if (!Directory.Exists(destinationRoot)
                        && await SourceSnapshotStillMatchesAsync(copySnapshot))
''',
)
replace_once(
    file_mover,
    '''            }
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
''',
    '''            }
''',
)

hooks = Path("listenarr.infrastructure/FileSystem/FileMover.TestHooks.cs")
replace_once(
    hooks,
    "    internal Func<string, Task>? BeforeEmptyDestinationPlaceholderQuarantineForTestAsync { get; init; }\n",
    "",
)

placeholder = Path("listenarr.infrastructure/FileSystem/FileMover.EmptyDestinationPlaceholder.cs")
if not placeholder.exists():
    raise RuntimeError("Empty destination placeholder source is missing")
placeholder.unlink()
