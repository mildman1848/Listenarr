from pathlib import Path

hierarchy_path = Path("listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.Hierarchy.cs")
hierarchy = hierarchy_path.read_text(encoding="utf-8")
if "internal async Task CopyNewFileFromAsync(" not in hierarchy:
    old = """        internal PinnedDirectoryCreation TryCreateChild(string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(childPath);
            EnsureVisiblePathMatches();
            return TryCreateRelative(_handle, FullPath, childName);
        }

        public void Dispose()
"""
    new = """        internal PinnedDirectoryCreation TryCreateChild(string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(childPath);
            EnsureVisiblePathMatches();
            return TryCreateRelative(_handle, FullPath, childName);
        }

        internal async Task CopyNewFileFromAsync(
            string sourcePath,
            string childName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            EnsureVisiblePathMatches();

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileHandle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, childName)
                : CreateRelativeFileUnix(_handle, childName);
            await using var destination = new FileStream(
                fileHandle,
                FileAccess.Write,
                bufferSize: 81920,
                isAsync: false);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            EnsureVisiblePathMatches();
        }

        public void Dispose()
"""
    if old not in hierarchy:
        raise RuntimeError("pinned anchor copy method anchor not found")
    hierarchy_path.write_text(hierarchy.replace(old, new, 1), encoding="utf-8")

directory_copy_path = Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.cs")
directory_copy = directory_copy_path.read_text(encoding="utf-8")
if "PinnedDirectoryAnchor stagingAnchor" not in directory_copy:
    old_call = """        var published = false;
        try
        {
            await PopulateDirectoryCopyStagingAsync(snapshot, stagingRoot);
            if (!stagingCreation.VisiblePathMatches()
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, stagingRoot)
"""
    new_call = """        var published = false;
        try
        {
            using var stagingAnchor = stagingCreation.OpenCreatedDirectoryAnchor();
            await PopulateDirectoryCopyStagingAsync(snapshot, stagingAnchor);
            if (!stagingCreation.VisiblePathMatches()
                || !stagingAnchor.VisiblePathMatches()
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, stagingRoot)
"""
    if old_call not in directory_copy:
        raise RuntimeError("directory copy staging call anchor not found")
    directory_copy = directory_copy.replace(old_call, new_call, 1)

    start = directory_copy.index("    private async Task PopulateDirectoryCopyStagingAsync(")
    end = directory_copy.index(
        "\n    private async Task<bool> SourceSnapshotStillMatchesAsync(",
        start,
    )
    replacement = """    private async Task PopulateDirectoryCopyStagingAsync(
        DirectoryCopySnapshot snapshot,
        PinnedDirectoryCreation.PinnedDirectoryAnchor stagingAnchor)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var anchors = new Dictionary<string, PinnedDirectoryCreation.PinnedDirectoryAnchor>(comparer)
        {
            [string.Empty] = stagingAnchor
        };
        var ownedAnchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
        try
        {
            foreach (var relativeDirectory in snapshot.RelativeDirectories)
            {
                var sourceDirectory = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeDirectory,
                    \"source directory\");
                if (!Directory.Exists(sourceDirectory)
                    || IsLinkedOrUnverifiableEntry(sourceDirectory))
                {
                    throw new IOException(
                        $\"Directory copy source changed after verification: {relativeDirectory}\");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeDirectory));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $\"Directory copy staging parent was not pinned: {relativeDirectory}\");
                }

                using var creation = parentAnchor.TryCreateChild(
                    Path.GetFileName(relativeDirectory));
                if (!creation.Created || !creation.VisiblePathMatches())
                {
                    throw new IOException(
                        $\"Directory copy staging was unexpectedly occupied: {relativeDirectory}\");
                }

                var childAnchor = creation.OpenCreatedDirectoryAnchor();
                if (!childAnchor.VisiblePathMatches())
                {
                    childAnchor.Dispose();
                    throw new IOException(
                        $\"Directory copy staging identity changed: {relativeDirectory}\");
                }

                anchors.Add(relativeDirectory, childAnchor);
                ownedAnchors.Add(childAnchor);
            }

            if (AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync != null)
            {
                await AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync(stagingAnchor.FullPath);
            }

            foreach (var relativeFile in snapshot.RelativeFiles)
            {
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    \"source file\");
                if (!File.Exists(sourceFile)
                    || Directory.Exists(sourceFile)
                    || IsLinkedOrUnverifiableEntry(sourceFile))
                {
                    throw new IOException(
                        $\"Directory copy source changed after verification: {relativeFile}\");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeFile));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $\"Directory copy staging file parent was not pinned: {relativeFile}\");
                }

                var childName = Path.GetFileName(relativeFile);
                await parentAnchor.CopyNewFileFromAsync(
                    sourceFile,
                    childName,
                    CancellationToken.None);
                var stagingFile = ResolveSnapshotPath(
                    stagingAnchor.FullPath,
                    relativeFile,
                    \"staging file\");
                if (IsLinkedOrUnverifiableEntry(stagingFile)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, stagingFile))
                {
                    throw new IOException(
                        $\"Directory copy staging content could not be verified: {relativeFile}\");
                }
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Copy,
                    sourceFile,
                    stagingFile,
                    \"Copied into an isolated pinned staging snapshot\");
            }
        }
        finally
        {
            for (var index = ownedAnchors.Count - 1; index >= 0; index--)
            {
                ownedAnchors[index].Dispose();
            }
        }
    }

    private static string NormalizeRelativeDirectoryKey(string? path) =>
        string.IsNullOrEmpty(path) || path == \".\"
            ? string.Empty
            : path;
"""
    directory_copy_path.write_text(
        directory_copy[:start] + replacement + directory_copy[end:],
        encoding="utf-8",
    )
