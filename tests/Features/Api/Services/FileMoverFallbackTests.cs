/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileMoverFallbackTests : IDisposable
    {
        private readonly string _root;

        public FileMoverFallbackTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
        }

        [Fact]
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

        [Fact]
        public async Task MoveFileAsync_SamePath_IsNoOpAndPreservesFile()
        {
            var file = Path.Join(_root, "same.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.MoveFileAsync(file, Path.GetFullPath(file));

            Assert.True(ok);
            Assert.True(File.Exists(file));
            Assert.Equal("content", await File.ReadAllTextAsync(file));
        }

        [Fact]
        public async Task PerformActionOn_MoveToSamePath_IsNoOpAndPreservesFile()
        {
            var file = Path.Join(_root, "perform-same.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.PerformActionOn(FileAction.Move, file, file);

            Assert.True(ok);
            Assert.True(File.Exists(file));
            Assert.Equal("content", await File.ReadAllTextAsync(file));
        }

        [Fact]
        public async Task MoveDirectoryAsync_SamePath_IsNoOpAndPreservesContents()
        {
            var directory = Path.Join(_root, "same-directory");
            Directory.CreateDirectory(directory);
            var file = Path.Join(directory, "track.mp3");
            await File.WriteAllTextAsync(file, "content");
            var mover = new FileMover(new NullLogger<FileMover>());

            var ok = await mover.MoveDirectoryAsync(directory, directory);

            Assert.True(ok);
            Assert.True(File.Exists(file));
        }

        [Fact]
        public async Task MoveDirectoryAsync_DestinationInsideSource_IsBlockedWithoutMutation()
        {
            var source = Path.Join(_root, "nested-source");
            var destination = Path.Join(source, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task MoveDirectoryAsync_SymbolicLinkAlias_BlocksCopyDeleteFallback()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path.Join(_root, "linked-source");
            var alias = Path.Join(_root, "linked-alias");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.MoveDirectoryAsync(source, alias);

            Assert.False(result);
            Assert.True(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task MoveDirectoryAsync_UnknownOverlap_BlocksCopyDeleteFallback()
        {
            var source = Path.Join(_root, "unknown-overlap-source");
            var destination = Path.Join(_root, "unknown-overlap-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var resolutionCallCount = 0;
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                {
                    var call = Interlocked.Increment(ref resolutionCallCount);
                    return ValueTask.FromResult(call == 1
                        ? new FileSystemSemanticsResolution(
                            FileSystemPathSemantics.CurrentHostDefault,
                            PathIdentityState.Valid,
                            Path.GetDirectoryName(path) ?? path)
                        : new FileSystemSemanticsResolution(
                            FileSystemPathSemantics.CurrentHostDefault,
                            PathIdentityState.Unavailable,
                            Path.GetDirectoryName(path) ?? path,
                            "simulated overlap probe failure"));
                });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(File.Exists(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task MoveFileAsync_UnavailableIdentityForSameFileAlias_PreservesSource()
        {
            var sourceFile = Path.Join(_root, "same-file.mp3");
            var aliasedDestination = Path.Join(_root, ".", "same-file.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        PathIdentityState.Unavailable,
                        Path.GetDirectoryName(path) ?? path,
                        "simulated probe failure")));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.MoveFileAsync(sourceFile, aliasedDestination);

            Assert.True(result);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("content", await File.ReadAllTextAsync(sourceFile));
        }

        [Fact]
        public async Task MoveFileAsync_SymbolicLinkDestinationToSource_PreservesFileContent()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var sourceFile = Path.Join(_root, "linked-file-source.mp3");
            var aliasedDestination = Path.Join(_root, "linked-file-alias.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            File.CreateSymbolicLink(aliasedDestination, sourceFile);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            await mover.MoveFileAsync(sourceFile, aliasedDestination);

            var survivingPath = File.Exists(sourceFile) ? sourceFile : aliasedDestination;
            Assert.True(File.Exists(survivingPath));
            Assert.Equal("content", await File.ReadAllTextAsync(survivingPath));
        }

        [Fact]
        public async Task MoveFileAsync_MovesFileSuccessfully()
        {
            var sourceFile = Path.Join(_root, "a.mp3");
            var destFile = Path.Join(_root, "b.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            var mover = new FileMover(new NullLogger<FileMover>());
            var ok = await mover.MoveFileAsync(sourceFile, destFile);

            Assert.True(ok);
            Assert.False(File.Exists(sourceFile));
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task MoveDirectoryAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var runner = new RecordingProcessRunner();
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

            var source = Path.Join(_root, "robocopy-source");
            var dest = Path.Join(_root, "robocopy-destination");
            Directory.CreateDirectory(Path.Join(source, "nested"));
            Directory.CreateDirectory(dest);
            await File.WriteAllTextAsync(Path.Join(source, "nested", "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(dest, "nested"), "destination conflict");

            var ok = await mover.MoveDirectoryAsync(source, dest);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(source, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(dest, runner.LastStartInfo.ArgumentList[1]);
            Assert.Contains("/MOVE", runner.LastStartInfo.ArgumentList);
            Assert.All(runner.LastStartInfo.ArgumentList, argument =>
            {
                Assert.False(argument.StartsWith("\"", StringComparison.Ordinal));
                Assert.False(argument.EndsWith("\"", StringComparison.Ordinal));
            });
        }

        [Fact]
        public async Task MoveFileAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var runner = new RecordingProcessRunner();
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

            var sourceFile = Path.Join(_root, "missing-file.mp3");
            var destFile = Path.Join(_root, "dest", "missing-file.mp3");

            var ok = await mover.MoveFileAsync(sourceFile, destFile);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(Path.GetDirectoryName(sourceFile) ?? string.Empty, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(Path.GetDirectoryName(destFile) ?? string.Empty, runner.LastStartInfo.ArgumentList[1]);
            Assert.Equal(Path.GetFileName(sourceFile), runner.LastStartInfo.ArgumentList[2]);
            Assert.Contains("/MOV", runner.LastStartInfo.ArgumentList);
        }

        private sealed class RecordingProcessRunner : IProcessRunner
        {
            public ProcessStartInfo? LastStartInfo { get; private set; }

            public Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, System.Threading.CancellationToken cancellationToken = default)
            {
                LastStartInfo = startInfo;
                return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty, false));
            }

            public Process StartProcess(ProcessStartInfo startInfo) => throw new NotSupportedException();

            public IDisposable RegisterTransientSensitive(IEnumerable<string> values) => new NoopDisposable();

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
