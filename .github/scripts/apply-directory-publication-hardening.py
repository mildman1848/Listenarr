from pathlib import Path

hierarchy_path = Path("listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.Hierarchy.cs")
hierarchy = hierarchy_path.read_text(encoding="utf-8")
old_visible = """        internal bool VisiblePathMatches()
        {
            ThrowIfDisposed();
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenDirectoryWindows(
                        FullPath,
                        openReparsePoint: !_followVisibleFinalLink)
                    : OpenDirectoryUnix(
                        FullPath,
                        noFollow: !_followVisibleFinalLink);
                return HandlesIdentifySameDirectory(_handle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }
"""
new_visible = """        internal bool VisiblePathMatches() =>
            VisiblePathMatches(FullPath, _followVisibleFinalLink);

        internal bool VisiblePathMatches(
            string visiblePath,
            bool followVisibleFinalLink = false)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(visiblePath);
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenDirectoryWindows(
                        visiblePath,
                        openReparsePoint: !followVisibleFinalLink)
                    : OpenDirectoryUnix(
                        visiblePath,
                        noFollow: !followVisibleFinalLink);
                return HandlesIdentifySameDirectory(_handle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }
"""
if old_visible not in hierarchy and new_visible not in hierarchy:
    raise RuntimeError("visible path identity anchor not found")
if old_visible in hierarchy:
    hierarchy = hierarchy.replace(old_visible, new_visible, 1)

old_handle = """            var fileHandle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, childName)
                : CreateRelativeFileUnix(_handle, childName);
            await using var destination = new FileStream(
"""
new_handle = """            using var fileHandle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, childName)
                : CreateRelativeFileUnix(_handle, childName);
            await using var destination = new FileStream(
"""
if old_handle not in hierarchy and new_handle not in hierarchy:
    raise RuntimeError("relative file handle ownership anchor not found")
if old_handle in hierarchy:
    hierarchy = hierarchy.replace(old_handle, new_handle, 1)
hierarchy_path.write_text(hierarchy, encoding="utf-8")

copy_path = Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.cs")
copy_text = copy_path.read_text(encoding="utf-8")
old_publish = """            try
            {
                Directory.Move(stagingRoot, destinationRoot);
                published = true;
            }
            catch (IOException) when (Directory.Exists(destinationRoot))
"""
new_publish = """            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed before publication.");
            }
            if (BeforeDirectoryCopyPublicationForTestAsync != null)
            {
                await BeforeDirectoryCopyPublicationForTestAsync(stagingRoot);
            }
            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed at the publication boundary.");
            }

            try
            {
                Directory.Move(stagingRoot, destinationRoot);
                published = true;
                if (!stagingAnchor.VisiblePathMatches(destinationRoot))
                {
                    throw new IOException(
                        "The published destination does not identify the pinned staging directory.");
                }
            }
            catch (IOException) when (!published && Directory.Exists(destinationRoot))
"""
if old_publish not in copy_text and new_publish not in copy_text:
    raise RuntimeError("directory publication anchor not found")
if old_publish in copy_text:
    copy_text = copy_text.replace(old_publish, new_publish, 1)

old_final = """            if (!await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
"""
new_final = """            if (!stagingAnchor.VisiblePathMatches(destinationRoot)
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
"""
if old_final not in copy_text and new_final not in copy_text:
    raise RuntimeError("final publication identity anchor not found")
if old_final in copy_text:
    copy_text = copy_text.replace(old_final, new_final, 1)
copy_path.write_text(copy_text, encoding="utf-8")
