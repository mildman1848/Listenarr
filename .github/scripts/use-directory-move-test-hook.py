from pathlib import Path

path = Path("tests/Features/Api/Services/FileMoverFallbackTests.cs")
text = path.read_text(encoding="utf-8")
old = '''            {
                BeforeDirectoryCopyPublicationForTestAsync = _ =>
                {
'''
new = '''            {
                BeforeDirectoryMoveAttemptForTest = () =>
                    throw new IOException("Force the verified directory fallback."),
                BeforeDirectoryCopyPublicationForTestAsync = _ =>
                {
'''
count = text.count(old)
if count != 1:
    raise RuntimeError(f"Expected one robocopy mover initializer, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
