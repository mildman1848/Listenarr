from pathlib import Path

path = Path("tests/Features/Api/Services/FileMoverFallbackTests.cs")
text = path.read_text(encoding="utf-8")
old = '''        [Fact]
        public async Task MoveDirectoryAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var source = Path.Join(_root, "robocopy-source");
            var dest = Path.Join(_root, "robocopy-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            Directory.CreateDirectory(dest);
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(dest, "nested"), "destination conflict");
            var runner = new RecordingProcessRunner(_ =>
            {
                File.Delete(Path.Join(dest, "nested"));
                Directory.CreateDirectory(Path.Join(dest, "nested"));
                File.Copy(
                    Path.Join(source, "nested", "book.m4b"),
                    Path.Join(dest, "nested", "book.m4b"));
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            var ok = await mover.MoveDirectoryAsync(source, dest);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(source, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(dest, runner.LastStartInfo.ArgumentList[1]);
            Assert.Contains("/E", runner.LastStartInfo.ArgumentList);
            Assert.DoesNotContain("/MOVE", runner.LastStartInfo.ArgumentList);
            Assert.All(runner.LastStartInfo.ArgumentList, argument =>
            {
                Assert.False(argument.StartsWith("\\\"", StringComparison.Ordinal));
                Assert.False(argument.EndsWith("\\\"", StringComparison.Ordinal));
            });
        }
'''
new = '''        [Fact]
        public async Task MoveDirectoryAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var source = Path.Join(_root, "robocopy-source");
            var dest = Path.Join(_root, "robocopy-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            var runner = new RecordingProcessRunner(_ =>
            {
                Directory.CreateDirectory(Path.Join(dest, "nested"));
                File.Copy(
                    Path.Join(source, "nested", "book.m4b"),
                    Path.Join(dest, "nested", "book.m4b"));
            });
            var publicationHookRan = false;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver())
            {
                BeforeDirectoryCopyPublicationForTestAsync = _ =>
                {
                    publicationHookRan = true;
                    throw new IOException("Force the verified robocopy fallback.");
                }
            };

            var ok = await mover.MoveDirectoryAsync(source, dest);

            Assert.True(ok);
            Assert.True(publicationHookRan);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(source, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(dest, runner.LastStartInfo.ArgumentList[1]);
            Assert.Contains("/E", runner.LastStartInfo.ArgumentList);
            Assert.DoesNotContain("/MOVE", runner.LastStartInfo.ArgumentList);
            Assert.All(runner.LastStartInfo.ArgumentList, argument =>
            {
                Assert.False(argument.StartsWith("\\\"", StringComparison.Ordinal));
                Assert.False(argument.EndsWith("\\\"", StringComparison.Ordinal));
            });
        }

        [Fact]
        public async Task MoveDirectoryAsync_ConflictingDestination_DoesNotInvokeRobocopy()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var source = Path.Join(_root, "robocopy-conflict-source");
            var destination = Path.Join(_root, "robocopy-conflict-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "nested"), "foreign");
            var runner = new RecordingProcessRunner(_ => { });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            var ok = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(ok);
            Assert.Null(runner.LastStartInfo);
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(source, "nested", "book.m4b")));
            Assert.Equal("foreign", await File.ReadAllTextAsync(
                Path.Join(destination, "nested")));
        }
'''
count = text.count(old)
if count != 1:
    raise RuntimeError(f"Expected exactly one robocopy test block, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
