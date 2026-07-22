from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file_path = Path(path)
    text = file_path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one anchor in {path}, found {count}")
    file_path.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "listenarr.infrastructure/FileSystem/FileMover.cs",
    "                try\n                {\n                    Directory.Move(sourceDir, destDir);\n",
    "                try\n                {\n                    BeforeDirectoryMoveAttemptForTest?.Invoke();\n                    Directory.Move(sourceDir, destDir);\n",
)
replace_once(
    "listenarr.infrastructure/FileSystem/FileMover.TestHooks.cs",
    "public partial class FileMover\n{\n",
    "public partial class FileMover\n{\n    internal Action? BeforeDirectoryMoveAttemptForTest { get; init; }\n",
)
