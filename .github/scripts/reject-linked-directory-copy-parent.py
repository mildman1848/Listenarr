from pathlib import Path

path = Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.cs")
text = path.read_text(encoding="utf-8")
old = '''        var destinationParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationParent)
            || !Directory.Exists(destinationParent)
            || IsLinkedOrUnverifiableEntry(destinationParent))
        {
            throw new IOException(
                "Directory copy requires an existing, non-linked destination parent.");
        }
'''
new = '''        var destinationParent = Path.GetDirectoryName(destinationRoot);
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
'''
count = text.count(old)
if count != 1:
    raise RuntimeError(f"Expected one destination-parent safety block, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
