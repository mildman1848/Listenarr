from pathlib import Path

path = Path("listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.cs")
text = path.read_text(encoding="utf-8")
old = """        await using var stream = new FileStream(
            fileHandle,
            FileAccess.Write,
            bufferSize: 4096,
            isAsync: true);
"""
new = """        await using var stream = new FileStream(
            fileHandle,
            FileAccess.Write,
            bufferSize: 4096,
            isAsync: false);
"""
if new in text:
    raise SystemExit(0)
if old not in text:
    raise RuntimeError("relative marker stream anchor not found")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
