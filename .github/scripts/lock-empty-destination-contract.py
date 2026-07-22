from pathlib import Path

path = Path("tests/Features/Api/Services/FileMoverFallbackTests.cs")
text = path.read_text(encoding="utf-8")
old = '''        [Fact]
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
new = '''        [Fact]
        public async Task MoveDirectoryAsync_UnownedEmptyDestination_IsRejectedWithoutMutation()
        {
            var source = Path.Join(_root, "sourceDir");
            var destination = Path.Join(_root, "destDir");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "track1.mp3"), "dummy");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal("dummy", await File.ReadAllTextAsync(
                Path.Join(source, "track1.mp3")));
            Assert.True(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(_root),
                directory => Path.GetFileName(directory).Contains(
                    ".listenarr-empty-placeholder-",
                    StringComparison.Ordinal));
        }
'''
count = text.count(old)
if count != 1:
    raise RuntimeError(f"Expected one empty destination test block, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
