from pathlib import Path

path = Path("tests/Features/Application/Downloads/Import/DownloadImportServiceTests.cs")
text = path.read_text(encoding="utf-8")
old = "            var filepaths = await _audiobookFileRepository.GetAllFilePathsAsync();\n"
new = (
    "            var filepaths = await _audiobookFileRepository.GetAllFilePathsAsync(\n"
    "                FileSystemPathSemantics.CurrentHostDefault);\n"
)
if text.count(old) != 1:
    raise RuntimeError(f"Expected one legacy call, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")

remaining = []
for root in ("listenarr.api", "listenarr.application", "listenarr.infrastructure", "tests"):
    for candidate in Path(root).rglob("*.cs"):
        candidate_text = candidate.read_text(encoding="utf-8")
        if "GetAllFilePathsAsync()" in candidate_text:
            remaining.append(str(candidate).replace("\\", "/"))
if remaining:
    raise RuntimeError("Zero-argument tracked path calls remain: " + ", ".join(remaining))
