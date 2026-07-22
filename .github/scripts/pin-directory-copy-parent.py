from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one anchor in {path}, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    Path("listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.Hierarchy.cs"),
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        var handle = OperatingSystem.IsWindows()
''',
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(boundaryPath);
        var handle = OperatingSystem.IsWindows()
''',
)

replace_once(
    Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.cs"),
    '''        var destinationParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationParent)
            || !Directory.Exists(destinationParent)
            || IsLinkedOrUnverifiableEntry(destinationParent)
            || !TryResolvePhysicalPath(destinationParent, out var parentResolution)
            || parentResolution.EntryKind != PhysicalPathEntryKind.Directory
            || parentResolution.EncounteredLink)
        {
            throw new IOException(
                "Directory copy requires an existing destination parent with no linked path components.");
        }

        var stagingName = $".{Path.GetFileName(destinationRoot)}.listenarr-copy-{Guid.NewGuid():N}";
        var stagingRoot = Path.Join(destinationParent, stagingName);
        using var stagingCreation = ExclusiveDirectoryCreator.TryCreatePinned(
            destinationParent,
            stagingName);
''',
    '''        var destinationParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationParent)
            || !Directory.Exists(destinationParent))
        {
            throw new IOException(
                "Directory copy requires an existing destination parent.");
        }

        using var destinationParentAnchor =
            PinnedDirectoryCreation.OpenPinnedBoundary(destinationParent);
        if (!destinationParentAnchor.VisiblePathMatches()
            || IsLinkedOrUnverifiableEntry(destinationParent)
            || !TryResolvePhysicalPath(destinationParent, out var parentResolution)
            || parentResolution.EntryKind != PhysicalPathEntryKind.Directory
            || parentResolution.EncounteredLink)
        {
            throw new IOException(
                "Directory copy requires a pinned destination parent with no linked path components.");
        }

        var stagingName = $".{Path.GetFileName(destinationRoot)}.listenarr-copy-{Guid.NewGuid():N}";
        var stagingRoot = Path.Join(destinationParent, stagingName);
        using var stagingCreation = destinationParentAnchor.TryCreateChild(stagingName);
''',
)
