from pathlib import Path

path = Path("tests/Features/Api/Services/FileMoverFallbackTests.cs")
text = path.read_text(encoding="utf-8")

old_first = '''        [Fact]
        public async Task MoveDirectoryAsync_WhenDestinationExists_UsesCopyAndDeleteFallback()
        {
            var source = Path.Join(_root, "sourceDir");
            var dest = Path.Join(_root, "destDir");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dest); // cause Directory.Move to throw (destination exists)

            var fileInSource = Path.Join(source, "track1.mp3");
            await File.WriteAllTextAsync(fileInSource, "dummy");

            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, dest);

            Assert.True(result, "MoveDirectoryAsync should succeed via fallback");
            // Source should be removed
            Assert.False(Directory.Exists(source));
            // Destination should contain the file
            var copied = Path.Join(dest, "track1.mp3");
            Assert.True(File.Exists(copied));
        }
'''
new_first = '''        [Fact]
        public async Task MoveDirectoryAsync_WhenDestinationIsEmpty_UsesVerifiedCopyAndDeleteFallback()
        {
            var source = Path.Join(_root, "sourceDir");
            var destination = Path.Join(_root, "destDir");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);

            var fileInSource = Path.Join(source, "track1.mp3");
            await File.WriteAllTextAsync(fileInSource, "dummy");

            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.True(result, "MoveDirectoryAsync should safely replace an empty placeholder");
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "dummy",
                await File.ReadAllTextAsync(Path.Join(destination, "track1.mp3")));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(_root),
                directory => Path.GetFileName(directory).Contains(
                    ".listenarr-empty-placeholder-",
                    StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveDirectoryAsync_EmptyDestinationReplacedBeforeQuarantine_PreservesReplacement()
        {
            var source = Path.Join(_root, "placeholder-race-source");
            var destination = Path.Join(_root, "placeholder-race-destination");
            var displacedPlaceholder = Path.Join(_root, "placeholder-race-displaced");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "track1.mp3"), "source");
            var hookRan = false;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeEmptyDestinationPlaceholderQuarantineForTestAsync = async path =>
                {
                    Assert.Equal(destination, path);
                    Directory.Move(destination, displacedPlaceholder);
                    Directory.CreateDirectory(destination);
                    await File.WriteAllTextAsync(Path.Join(destination, "foreign.txt"), "foreign");
                    hookRan = true;
                }
            };

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(hookRan);
            Assert.Equal("source", await File.ReadAllTextAsync(Path.Join(source, "track1.mp3")));
            Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Join(destination, "foreign.txt")));
            Assert.True(Directory.Exists(displacedPlaceholder));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(_root),
                directory => Path.GetFileName(directory).Contains(
                    ".listenarr-empty-placeholder-",
                    StringComparison.Ordinal));
        }
'''
if text.count(old_first) != 1:
    raise RuntimeError(f"Expected one empty-destination test anchor, found {text.count(old_first)}")
text = text.replace(old_first, new_first, 1)

old_mutation = '''        [Fact]
        public async Task CopyDirectoryAsync_SourceMutationAfterPreflight_IsNotRecursivelyEnumerated()
        {
            var source = Path.Join(_root, "copy-snapshot-source");
            var destination = Path.Join(_root, "copy-snapshot-destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "original.m4b"), "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryCopyPreflightForTestAsync = async () =>
                {
                    var lateDirectory = Path.Join(source, "late");
                    Directory.CreateDirectory(lateDirectory);
                    await File.WriteAllTextAsync(Path.Join(lateDirectory, "late.m4b"), "late");
                }
            };

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Join(destination, "original.m4b")));
            Assert.False(File.Exists(Path.Join(destination, "late", "late.m4b")));
            Assert.Equal("late", await File.ReadAllTextAsync(Path.Join(source, "late", "late.m4b")));
        }
'''
new_mutation = '''        [Fact]
        public async Task CopyDirectoryAsync_SourceMutationAfterPreflight_FailsWithoutPublishingSnapshot()
        {
            var source = Path.Join(_root, "copy-snapshot-source");
            var destination = Path.Join(_root, "copy-snapshot-destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "original.m4b"), "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDirectoryCopyPreflightForTestAsync = async () =>
                {
                    var lateDirectory = Path.Join(source, "late");
                    Directory.CreateDirectory(lateDirectory);
                    await File.WriteAllTextAsync(Path.Join(lateDirectory, "late.m4b"), "late");
                }
            };

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Join(source, "original.m4b")));
            Assert.Equal("late", await File.ReadAllTextAsync(Path.Join(source, "late", "late.m4b")));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(_root),
                directory => Path.GetFileName(directory).Contains(
                    ".listenarr-copy-",
                    StringComparison.Ordinal));
        }
'''
if text.count(old_mutation) != 1:
    raise RuntimeError(f"Expected one source-mutation test anchor, found {text.count(old_mutation)}")
path.write_text(text.replace(old_mutation, new_mutation, 1), encoding="utf-8")
