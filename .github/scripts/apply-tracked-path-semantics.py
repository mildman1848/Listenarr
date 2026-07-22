from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file_path = Path(path)
    text = file_path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one anchor in {path}, found {count}: {old!r}")
    file_path.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "listenarr.application/Audiobooks/Contracts/Repositories/IAudiobookFileRepository.cs",
    "        Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default);\n",
    "        Task<List<string>> GetAllFilePathsAsync(\n"
    "            FileSystemPathSemantics comparisonSemantics,\n"
    "            CancellationToken ct = default);\n",
)

replace_once(
    "listenarr.infrastructure/Persistence/Repositories/EfAudiobookFileRepository.cs",
    "        public async Task<List<string>> GetAllFilePathsAsync(CancellationToken ct = default)\n",
    "        public async Task<List<string>> GetAllFilePathsAsync(\n"
    "            FileSystemPathSemantics comparisonSemantics,\n"
    "            CancellationToken ct = default)\n",
)
replace_once(
    "listenarr.infrastructure/Persistence/Repositories/EfAudiobookFileRepository.cs",
    "                    files,\n"
    "                    audiobooks,\n"
    "                    FileSystemPathSemantics.CurrentHostDefault)\n",
    "                    files,\n"
    "                    audiobooks,\n"
    "                    comparisonSemantics)\n",
)

replace_once(
    "listenarr.infrastructure/Library/Scanning/UnmatchedScanBackgroundService.cs",
    "            var trackedFromFiles = await fileRepository.GetAllFilePathsAsync(ct);\n",
    "            var trackedFromFiles = await fileRepository.GetAllFilePathsAsync(semantics, ct);\n",
)

replace_once(
    "listenarr.api/Features/Library/RootFoldersController.cs",
    "                // Filter out items already added to the library since the scan ran\n"
    "                var trackedFromFiles = await _fileRepository.GetAllFilePathsAsync();\n"
    "                var trackedFromAudiobooks = (await _audiobookRepository.GetAllAsync())\n",
    "                // Filter out items already added to the library since the scan ran\n"
    "                var trackedPathSemantics = await ResolveFolderSemanticsAsync(folder);\n"
    "                var trackedFromFiles = await _fileRepository.GetAllFilePathsAsync(\n"
    "                    trackedPathSemantics);\n"
    "                var trackedFromAudiobooks = (await _audiobookRepository.GetAllAsync())\n",
)
replace_once(
    "listenarr.api/Features/Library/RootFoldersController.cs",
    "                    .Select(a => a.FilePath!)\n"
    "                    .ToList();\n"
    "                var trackedPathSemantics = await ResolveFolderSemanticsAsync(folder);\n"
    "                var tracked = trackedFromFiles\n",
    "                    .Select(a => a.FilePath!)\n"
    "                    .ToList();\n"
    "                var tracked = trackedFromFiles\n",
)

replace_once(
    "tests/Features/Infrastructure/Repositories/EfAudiobookFileTrackedPathTests.cs",
    "        var paths = await repository.GetAllFilePathsAsync();\n",
    "        var paths = await repository.GetAllFilePathsAsync(\n"
    "            FileSystemPathSemantics.CurrentHostDefault);\n",
)

remaining = []
for root in ("listenarr.api", "listenarr.application", "listenarr.infrastructure", "tests"):
    for file_path in Path(root).rglob("*.cs"):
        text = file_path.read_text(encoding="utf-8")
        if "GetAllFilePathsAsync(" in text:
            remaining.append(str(file_path).replace("\\", "/"))
print("GetAllFilePathsAsync references:")
for path in sorted(remaining):
    print(path)
