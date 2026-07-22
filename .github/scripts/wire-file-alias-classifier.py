from pathlib import Path

paths = [
    Path("listenarr.infrastructure/FileSystem/FileMover.Copying.cs"),
    Path("listenarr.infrastructure/FileSystem/FileMover.FileMoveLocks.cs"),
]
replacements = 0
for path in paths:
    text = path.read_text(encoding="utf-8")
    count = text.count("IsLinkedFilesystemAliasAsync")
    if count == 0:
        raise RuntimeError(f"No alias classifier calls found in {path}")
    replacements += count
    text = text.replace("IsLinkedFilesystemAliasAsync", "IsFilesystemAliasAsync")
    text = text.replace(
        "source and destination are linked aliases",
        "source and destination are filesystem aliases")
    path.write_text(text, encoding="utf-8")

if replacements != 6:
    raise RuntimeError(f"Expected six alias classifier call sites, found {replacements}")
