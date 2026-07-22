from pathlib import Path

path = Path("listenarr.infrastructure/FileSystem/FileMover.cs")
text = path.read_text(encoding="utf-8")

old_start = '''                        var startInfo = CreateRobocopyStartInfo(
                            sourceDir,
                            destDir,
'''
new_start = '''                        var startInfo = CreateRobocopyStartInfo(
                            sourceDir,
                            destinationRoot,
'''
if text.count(old_start) != 1:
    raise RuntimeError(f"Expected one robocopy destination anchor, found {text.count(old_start)}")
text = text.replace(old_start, new_start, 1)

old_cleanup = '''                            var cleanup = await CleanupCopiedSourceTreeAsync(
                                sourceDir,
                                destDir);
'''
new_cleanup = '''                            var cleanup = await CleanupCopiedSourceTreeAsync(
                                sourceDir,
                                destinationRoot);
'''
if text.count(old_cleanup) != 1:
    raise RuntimeError(f"Expected one robocopy cleanup anchor, found {text.count(old_cleanup)}")
path.write_text(text.replace(old_cleanup, new_cleanup, 1), encoding="utf-8")
