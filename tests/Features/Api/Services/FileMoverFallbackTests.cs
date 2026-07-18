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
        public async Task MoveFileAsync_VerifiedCopyFallback_RemovesSourceBeforeReportingSuccess()
        {
            var sourceFile = Path.Join(_root, "fallback-source.mp3");
            var destinationFile = Path.Join(_root, "fallback-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("content", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "*.listenarr-move-*.partial",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public async Task MoveFileAsync_VerifiedCopyFallback_SourceDeleteFailureReportsFailure()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var sourceFile = Path.Join(_root, "retained-source.mp3");
            var destinationFile = Path.Join(_root, "published-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            bool moved;
            using (File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                moved = await mover.MoveFileAsync(sourceFile, destinationFile);
            }

            Assert.False(moved);
            Assert.Equal("content", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("content", await File.ReadAllTextAsync(destinationFile));
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
        public async Task MoveDirectoryAsync_UnattributedCleanupArtifactIsPreservedAndBlocked()
        {
            var source = Path.Join(_root, "interrupted-cleanup-source");
            var destination = Path.Join(_root, "interrupted-cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var quarantine = Path.Join(
                source,
                $"book.m4b.listenarr-copy-cleanup-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(quarantine, "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(moved);
            Assert.True(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(quarantine));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }

        [Fact]
        public async Task MoveDirectoryAsync_ConflictingInterruptedCleanupIsPreservedAndBlocked()
        {
            var source = Path.Join(_root, "conflicting-cleanup-source");
            var destination = Path.Join(_root, "conflicting-cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var original = Path.Join(source, "book.m4b");
            var quarantine = $"{original}.listenarr-copy-cleanup-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(original, "new content");
            await File.WriteAllTextAsync(quarantine, "original content");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver());

            var moved = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(moved);
            Assert.Equal("new content", await File.ReadAllTextAsync(original));
            Assert.Equal("original content", await File.ReadAllTextAsync(quarantine));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_UnexpectedSourceContentIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-source");
            var destination = Path.Join(_root, "cleanup-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(source, "arrived-late.txt"), "preserve");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination);

            Assert.False(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_ContentArrivingAfterVerificationIsPreserved()
        {
            var source = Path.Join(_root, "cleanup-late-source");
            var destination = Path.Join(_root, "cleanup-late-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () => File.WriteAllText(
                    Path.Join(source, "arrived-late.txt"),
                    "preserve"));

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.False(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CleanupCopiedSourceTreeAsync_SourceChangeAfterVerificationIsRestored()
        {
            var source = Path.Join(_root, "cleanup-changed-source");
            var destination = Path.Join(_root, "cleanup-changed-destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var sourceFile = Path.Join(source, "book.m4b");
            var destinationFile = Path.Join(destination, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(new NullLogger<FileMover>());

            var cleanup = await mover.CleanupCopiedSourceTreeAsync(
                source,
                destination,
                () => File.WriteAllText(sourceFile, "changed"));

            Assert.True(cleanup.DestinationVerified);
            Assert.False(cleanup.SourceRemoved);
            Assert.Equal("changed", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                source,
                "*.listenarr-copy-cleanup-*",
                SearchOption.TopDirectoryOnly));
        }

        [Fact]
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
                Assert.False(argument.StartsWith("\"", StringComparison.Ordinal));
                Assert.False(argument.EndsWith("\"", StringComparison.Ordinal));
            });
        }

        [Fact]
        public void FileMoveStagingValidation_RejectsOrdinaryDirectoryReplacementWithoutToken()
        {
            var destinationDirectory = Path.Join(_root, "staging-replacement-destination");
            var stagingDirectory = Path.Join(destinationDirectory, ".listenarr-file-move-test");
            Directory.CreateDirectory(stagingDirectory);
            const string ownershipToken = "0123456789abcdef0123456789abcdef";
            File.WriteAllText(
                Path.Join(stagingDirectory, FileMover.FileMoveStagingMarkerName),
                ownershipToken);

            Assert.True(FileMover.TryValidateOwnedFileMoveStagingDirectory(
                stagingDirectory,
                destinationDirectory,
                ownershipToken,
                out var normalizedStagingDirectory));
            Assert.Equal(Path.GetFullPath(stagingDirectory), normalizedStagingDirectory);

            Directory.Delete(stagingDirectory, recursive: true);
            Directory.CreateDirectory(stagingDirectory);
            File.WriteAllText(Path.Join(stagingDirectory, "foreign.txt"), "foreign");

            Assert.False(FileMover.TryValidateOwnedFileMoveStagingDirectory(
                stagingDirectory,
                destinationDirectory,
                ownershipToken,
                out _));
            Assert.Equal(
                "foreign",
                File.ReadAllText(Path.Join(stagingDirectory, "foreign.txt")));
        }

        [Fact]
        public void FileMoveStagingValidation_RejectsSymlinkReplacementEvenWithCopiedToken()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var destinationDirectory = Path.Join(_root, "staging-link-destination");
            var stagingDirectory = Path.Join(destinationDirectory, ".listenarr-file-move-test");
            var outsideDirectory = Path.Join(_root, "staging-link-outside");
            Directory.CreateDirectory(destinationDirectory);
            Directory.CreateDirectory(outsideDirectory);
            const string ownershipToken = "fedcba9876543210fedcba9876543210";
            File.WriteAllText(
                Path.Join(outsideDirectory, FileMover.FileMoveStagingMarkerName),
                ownershipToken);
            File.WriteAllText(Path.Join(outsideDirectory, "foreign.txt"), "foreign");
            Directory.CreateSymbolicLink(stagingDirectory, outsideDirectory);

            Assert.False(FileMover.TryValidateOwnedFileMoveStagingDirectory(
                stagingDirectory,
                destinationDirectory,
                ownershipToken,
                out _));
            Assert.Equal(
                "foreign",
                File.ReadAllText(Path.Join(outsideDirectory, "foreign.txt")));
        }

        [Fact]
        public async Task MoveFileAsync_RobocopyFallback_RecreatedStagingDirectoryIsPreservedAndRejected()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var sourceFile = Path.Join(_root, "robocopy-replaced-source.mp3");
            var destinationFile = Path.Join(_root, "robocopy-replaced-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "source");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            string? replacedStagingDirectory = null;
            var runner = new RecordingProcessRunner(startInfo =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
                replacedStagingDirectory = startInfo.ArgumentList[1];
                Directory.Delete(replacedStagingDirectory, recursive: true);
                Directory.CreateDirectory(replacedStagingDirectory);
                File.WriteAllText(
                    Path.Join(replacedStagingDirectory, Path.GetFileName(sourceFile)),
                    "foreign");
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

                Assert.False(moved);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(destinationFile));
                Assert.NotNull(replacedStagingDirectory);
                Assert.Equal(
                    "foreign",
                    await File.ReadAllTextAsync(Path.Join(
                        replacedStagingDirectory!,
                        Path.GetFileName(sourceFile))));
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        [Fact]
        public async Task MoveFileAsync_RobocopyFallback_UsesVerifiedCopyOnlyStaging()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var sourceFile = Path.Join(_root, "robocopy-source.mp3");
            var destFile = Path.Join(_root, "dest", "renamed-destination.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await File.WriteAllTextAsync(sourceFile, "content");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var runner = new RecordingProcessRunner(startInfo =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
                File.Copy(
                    sourceFile,
                    Path.Join(
                        startInfo.ArgumentList[1],
                        startInfo.ArgumentList[2]));
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var ok = await mover.MoveFileAsync(sourceFile, destFile);

                Assert.True(ok);
                Assert.False(File.Exists(sourceFile));
                Assert.Equal("content", await File.ReadAllTextAsync(destFile));
                Assert.NotNull(runner.LastStartInfo);
                Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
                Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
                Assert.Equal(
                    Path.GetDirectoryName(sourceFile) ?? string.Empty,
                    runner.LastStartInfo.ArgumentList[0]);
                Assert.StartsWith(
                    Path.GetDirectoryName(destFile) ?? string.Empty,
                    runner.LastStartInfo.ArgumentList[1],
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    Path.GetFileName(sourceFile),
                    runner.LastStartInfo.ArgumentList[2]);
                Assert.Contains("/COPY:DAT", runner.LastStartInfo.ArgumentList);
                Assert.DoesNotContain("/MOV", runner.LastStartInfo.ArgumentList);
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        [Fact]
        public async Task MoveFileAsync_RobocopyExitCodeWithoutStagedFileReportsFailure()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var sourceFile = Path.Join(_root, "robocopy-unverified-source.mp3");
            var destinationFile = Path.Join(_root, "robocopy-unverified-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");
            FileStream? sourceLock = File.Open(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var runner = new RecordingProcessRunner(_ =>
            {
                sourceLock?.Dispose();
                sourceLock = null;
            });
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 0,
                    RobocopyTimeoutMs = 1000,
                }),
                new FileSystemSemanticsResolver());

            try
            {
                var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

                Assert.False(moved);
                Assert.True(File.Exists(sourceFile));
                Assert.False(File.Exists(destinationFile));
            }
            finally
            {
                sourceLock?.Dispose();
            }
        }

        private sealed class RecordingProcessRunner(
            Action<ProcessStartInfo>? onRun = null) : IProcessRunner
        {
            public ProcessStartInfo? LastStartInfo { get; private set; }

            public Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, System.Threading.CancellationToken cancellationToken = default)
            {
                LastStartInfo = startInfo;
                onRun?.Invoke(startInfo);
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
