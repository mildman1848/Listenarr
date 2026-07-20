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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "FileMoverFallbackTests")]
    [Trait("Category", "FileSystem")]
    public sealed class FileMoverFallbackTests : BaseTests, IDisposable
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
            var treePreflightCalls = 0;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeDirectoryTreePreflightForTest = () => treePreflightCalls++
            };

            var result = await mover.MoveDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal(0, treePreflightCalls);
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
        public async Task CopyDirectoryAsync_EquivalentPath_IsRejectedWithoutMutation()
        {
            var source = Path.Join(_root, "copy-same-source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, Path.Join(source, "."));

            Assert.False(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_DestinationInsideSource_IsRejectedBeforeCreatingArtifacts()
        {
            var source = Path.Join(_root, "copy-parent-source");
            var destination = Path.Join(source, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var treePreflightCalls = 0;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                BeforeDirectoryTreePreflightForTest = () => treePreflightCalls++
            };

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal(0, treePreflightCalls);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_SourceInsideDestination_IsRejectedWithoutWritingDestination()
        {
            var destination = Path.Join(_root, "copy-containing-destination");
            var source = Path.Join(destination, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var unrelated = Path.Join(destination, "unrelated.txt");
            await File.WriteAllTextAsync(unrelated, "preserve");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
            Assert.False(File.Exists(Path.Join(destination, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_UnavailableFilesystemSemantics_FailsClosedWithoutArtifacts()
        {
            var source = Path.Join(_root, "copy-unknown-source");
            var destination = Path.Join(_root, "copy-unknown-destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
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
                        "simulated unavailable semantics")));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_CaseAliasUnderInsensitiveSemantics_IsRejected()
        {
            var source = Path.Join(_root, "CopyCaseSource");
            var destination = Path.Join(_root, "copycasesource", "nested");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            FileSystemCaseSensitivity.Insensitive),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path)));
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: semanticsResolver.Object);

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
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

        [Fact]
        public async Task CopyDirectoryAsync_SiblingPrefix_IsAllowed()
        {
            var source = Path.Join(_root, "copy-book");
            var destination = Path.Join(_root, "copy-book-expanded");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.True(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_MissingDestinationBelowSymlinkedParent_IsRejected()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path.Join(_root, "copy-linked-parent-source");
            var alias = Path.Join(_root, "copy-linked-parent-alias");
            var destination = Path.Join(alias, "nested", "target");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, destination);

            Assert.False(result);
            Assert.False(Directory.Exists(Path.Join(source, "nested")));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        }

        [Fact]
        public async Task CopyDirectoryAsync_SymbolicLinkAlias_IsRejectedWhereSupported()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var source = Path.Join(_root, "copy-linked-source");
            var alias = Path.Join(_root, "copy-linked-alias");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
            Directory.CreateSymbolicLink(alias, source);
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var result = await mover.CopyDirectoryAsync(source, alias);

            Assert.False(result);
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
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
        public async Task MoveFileAsync_CopyFallback_SourceRecreatedAfterClaim_IsPreserved()
        {
            var sourceFile = Path.Join(_root, "fallback-swap-source.mp3");
            var destinationFile = Path.Join(_root, "fallback-swap-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (claimedSource, quarantinePath) =>
                {
                    Assert.Equal(sourceFile, claimedSource);
                    Assert.True(File.Exists(quarantinePath));
                    await File.WriteAllTextAsync(sourceFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_root),
                path => path.Contains(".listenarr-copy-cleanup-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveFileAsync_IdempotentDestination_SourceRecreatedAfterClaim_IsPreserved()
        {
            var sourceFile = Path.Join(_root, "idempotent-swap-source.mp3");
            var destinationFile = Path.Join(_root, "idempotent-swap-destination.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (claimedSource, quarantinePath) =>
                {
                    Assert.Equal(sourceFile, claimedSource);
                    Assert.True(File.Exists(quarantinePath));
                    await File.WriteAllTextAsync(sourceFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_root),
                path => path.Contains(".listenarr-copy-cleanup-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MoveFileAsync_DestinationRecreatedDuringCommit_IsAtomicallyReplaced()
        {
            var sourceFile = Path.Join(_root, "destination-swap-source.mp3");
            var destinationFile = Path.Join(_root, "destination-swap-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationQuarantinedForTestAsync = async (claimedDestination, claimPath) =>
                {
                    Assert.Equal(destinationFile, claimedDestination);
                    Assert.True(File.Exists(claimPath));
                    await File.WriteAllTextAsync(destinationFile, "replacement");
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterSourceClaim_RecoversOnRetry()
        {
            var sourceFile = Path.Join(_root, "interrupted-claim-source.mp3");
            var destinationFile = Path.Join(_root, "interrupted-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "*.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_ReverseDirectionCannotBypassInterruptedState()
        {
            var firstPath = Path.Join(_root, "reverse-state-first.mp3");
            var secondPath = Path.Join(_root, "reverse-state-second.mp3");
            await File.WriteAllTextAsync(firstPath, "original");
            await File.WriteAllTextAsync(secondPath, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated directional interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(firstPath, secondPath));

            var sourceClaim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));

            var reversed = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(secondPath, firstPath);

            Assert.False(reversed);
            Assert.False(File.Exists(firstPath));
            Assert.Equal("original", await File.ReadAllTextAsync(secondPath));
            Assert.Equal("original", await File.ReadAllTextAsync(sourceClaim));
        }

        [Fact]
        public async Task MoveFileAsync_UnexpectedRecoveryState_IsPreservedAndBlocked()
        {
            var sourceFile = Path.Join(_root, "ambiguous-state-source.mp3");
            var destinationFile = Path.Join(_root, "ambiguous-state-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated ambiguous-state interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var sourceStateDirectory = Assert.Single(Directory.EnumerateDirectories(
                _root,
                ".listenarr-file-source-*.state",
                SearchOption.TopDirectoryOnly));
            var unexpectedPath = Path.Join(sourceStateDirectory, "unexpected.txt");
            await File.WriteAllTextAsync(unexpectedPath, "preserve");

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Equal("preserve", await File.ReadAllTextAsync(unexpectedPath));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_DestinationRecreatedAfterVerification_IsAtomicallyReplaced()
        {
            var sourceFile = Path.Join(_root, "verified-swap-source.mp3");
            var destinationFile = Path.Join(_root, "verified-swap-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceClaimDeletedForTestAsync = () =>
                {
                    File.WriteAllText(destinationFile, "replacement");
                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_MissingClaimBeforeRetirementFence_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "missing-claim-source.mp3");
            var destinationFile = Path.Join(_root, "missing-claim-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationStateCreatedForTestAsync = () =>
                    throw new OperationCanceledException("simulated destination-state interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var sourceClaim = Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            File.Delete(sourceClaim);

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_OpenHandleMutationAfterStaging_IsRestoredAndFailsClosed()
        {
            var sourceFile = Path.Join(_root, "open-handle-source.mp3");
            var destinationFile = Path.Join(_root, "open-handle-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "different");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationQuarantinedForTestAsync = async (_, _) =>
                {
                    var claimedSource = Assert.Single(Directory.EnumerateFiles(
                        _root,
                        "source.claim",
                        SearchOption.AllDirectories));
                    await using var claimedHandle = new FileStream(
                        claimedSource,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite | FileShare.Delete);
                    claimedHandle.Position = 0;
                    await claimedHandle.WriteAsync("changed!"u8.ToArray());
                    await claimedHandle.FlushAsync();
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("changed!", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterDestinationStage_RecoversOnRetry()
        {
            var sourceFile = Path.Join(_root, "stage-crash-source.mp3");
            var destinationFile = Path.Join(_root, "stage-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated stage interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterRetirementFence_PreservesReplacementAndCompletesRecovery()
        {
            var sourceFile = Path.Join(_root, "fence-crash-source.mp3");
            var destinationFile = Path.Join(_root, "fence-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceRetirementCommittedForTestAsync = () =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    throw new OperationCanceledException("simulated retirement-fence interruption");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "replacement-generation.fence",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterSourceRetirement_CompletesRecovery()
        {
            var sourceFile = Path.Join(_root, "retired-crash-source.mp3");
            var destinationFile = Path.Join(_root, "retired-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceClaimDeletedForTestAsync = () =>
                    throw new OperationCanceledException("simulated retirement interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            var retryMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var retried = await retryMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_SourceCreatedAfterRetirementBeforePublication_IsPreservedOnRetry()
        {
            var sourceFile = Path.Join(_root, "retirement-replacement-source.mp3");
            var destinationFile = Path.Join(_root, "retirement-replacement-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceClaimDeletedForTestAsync = () =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    throw new OperationCanceledException("simulated post-retirement interruption");
                }
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "destination.stage",
                SearchOption.AllDirectories));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedAfterStateCleanup_RetryFailsWithoutFalseCompletion()
        {
            var sourceFile = Path.Join(_root, "cleaned-crash-source.mp3");
            var destinationFile = Path.Join(_root, "cleaned-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated post-cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_MissingSourceWithExistingDestination_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "never-started-source.mp3");
            var destinationFile = Path.Join(_root, "unrelated-existing-target.mp3");
            await File.WriteAllTextAsync(destinationFile, "unrelated");

            var moved = await new FileMover(
                new NullLogger<FileMover>(),
                options: Options.Create(new FileMoverOptions { MaxRetries = 0 }),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(moved);
            Assert.Equal("unrelated", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_RecreatedSourceSurvivesPostCleanupCrashAndRetry()
        {
            var sourceFile = Path.Join(_root, "fenced-source.mp3");
            var destinationFile = Path.Join(_root, "fenced-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                {
                    File.WriteAllText(sourceFile, "replacement");
                    return Task.CompletedTask;
                },
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated fenced cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Single(Directory.EnumerateFiles(
                _root,
                "replacement-generation.fence",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_SourceCreatedAfterPublication_IsFencedBeforeCleanup()
        {
            var sourceFile = Path.Join(_root, "late-fenced-source.mp3");
            var destinationFile = Path.Join(_root, "late-fenced-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterDestinationPublishedForTestAsync = path =>
                {
                    Assert.Equal(destinationFile, path);
                    File.WriteAllText(sourceFile, "replacement");
                    return Task.CompletedTask;
                },
                AfterFileMoveStateCleanedForTestAsync = () =>
                    throw new OperationCanceledException("simulated late fenced cleanup interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.True(retried);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_InterruptedBeforeSourceClaim_DoesNotInferCompletion()
        {
            var sourceFile = Path.Join(_root, "preclaim-crash-source.mp3");
            var destinationFile = Path.Join(_root, "preclaim-crash-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceStateCreatedForTestAsync = () =>
                    throw new OperationCanceledException("simulated preclaim interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => mover.MoveFileAsync(sourceFile, destinationFile));
            File.Delete(sourceFile);

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(sourceFile, destinationFile);

            Assert.False(retried);
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_UnrelatedPaths_DoNotSharePairGate()
        {
            var firstSource = Path.Join(_root, "gate-first-source.mp3");
            var firstDestination = Path.Join(_root, "gate-first-target.mp3");
            var secondSource = Path.Join(_root, "gate-second-source.mp3");
            var secondDestination = Path.Join(_root, "gate-second-target.mp3");
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(firstDestination, "first");
            await File.WriteAllTextAsync(secondSource, "second");
            await File.WriteAllTextAsync(secondDestination, "second");
            var firstClaimReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    firstClaimReached.SetResult();
                    await releaseFirstClaim.Task;
                }
            };

            var firstMove = firstMover.MoveFileAsync(firstSource, firstDestination);
            await firstClaimReached.Task;
            var secondMove = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(secondSource, secondDestination);

            Assert.True(await secondMove);
            releaseFirstClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(File.Exists(firstSource));
            Assert.False(File.Exists(secondSource));
        }

        [Fact]
        public async Task MoveFileAsync_ConcurrentSamePaths_AreSerializedByPathLocks()
        {
            var sourceFile = Path.Join(_root, "concurrent-source.mp3");
            var destinationFile = Path.Join(_root, "concurrent-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var claimReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    claimReached.SetResult();
                    await releaseClaim.Task;
                }
            };
            var firstMove = firstMover.MoveFileAsync(sourceFile, destinationFile);
            await claimReached.Task;
            var competingMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver());

            var competingMove = competingMover.MoveFileAsync(sourceFile, destinationFile);

            Assert.False(competingMove.IsCompleted);
            releaseClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(await competingMove);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
        }

        [Fact]
        public async Task MoveFileAsync_CaseAliasesShareResolvedEndpointLocks()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var sourceFile = Path.Join(_root, "case-lock-source.mp3");
            var destinationFile = Path.Join(_root, "case-lock-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var claimReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClaim = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = async (_, _) =>
                {
                    claimReached.SetResult();
                    await releaseClaim.Task;
                }
            };

            var firstMove = firstMover.MoveFileAsync(sourceFile, destinationFile);
            await claimReached.Task;
            var aliasedMove = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(
                    sourceFile.ToUpperInvariant(),
                    destinationFile.ToUpperInvariant());

            Assert.False(aliasedMove.IsCompleted);
            releaseClaim.SetResult();
            Assert.True(await firstMove);
            Assert.False(await aliasedMove);
        }

        [Fact]
        public async Task MoveFileAsync_CaseAliasRetryRecoversSameCrashState()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var sourceFile = Path.Join(_root, "case-recovery-source.mp3");
            var destinationFile = Path.Join(_root, "case-recovery-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated alias interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(sourceFile, destinationFile));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(
                    sourceFile.ToUpperInvariant(),
                    destinationFile.ToUpperInvariant());

            Assert.True(retried);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_LinkedParentAliasRetryRecoversSameCrashState()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var realDirectory = Path.Join(_root, "real-parent");
            var aliasDirectory = Path.Join(_root, "linked-parent");
            Directory.CreateDirectory(realDirectory);
            Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
            var realSource = Path.Join(realDirectory, "alias-source.mp3");
            var realDestination = Path.Join(realDirectory, "alias-target.mp3");
            await File.WriteAllTextAsync(realSource, "original");
            await File.WriteAllTextAsync(realDestination, "original");
            var interruptedMover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, _) =>
                    throw new OperationCanceledException("simulated linked-parent interruption")
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => interruptedMover.MoveFileAsync(
                    Path.Join(aliasDirectory, "alias-source.mp3"),
                    Path.Join(aliasDirectory, "alias-target.mp3")));

            var retried = await new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
                .MoveFileAsync(realSource, realDestination);

            Assert.True(retried);
            Assert.False(File.Exists(realSource));
            Assert.Equal("original", await File.ReadAllTextAsync(realDestination));
            Assert.Empty(Directory.EnumerateFiles(
                realDirectory,
                "source.claim",
                SearchOption.AllDirectories));
        }

        [Fact]
        public async Task MoveFileAsync_LongSourceName_UsesCompactClaimNames()
        {
            var longStem = new string('a', 180);
            var sourceFile = Path.Join(_root, $"{longStem}-source.mp3");
            var destinationFile = Path.Join(_root, $"{longStem}-target.mp3");
            await File.WriteAllTextAsync(sourceFile, "original");
            await File.WriteAllTextAsync(destinationFile, "original");
            string? observedClaimName = null;
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                AfterSourceQuarantinedForTestAsync = (_, claimPath) =>
                {
                    observedClaimName = Path.GetFileName(claimPath);
                    return Task.CompletedTask;
                }
            };

            var moved = await mover.MoveFileAsync(sourceFile, destinationFile);

            Assert.True(moved);
            Assert.NotNull(observedClaimName);
            Assert.True(observedClaimName.Length < 80);
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("original", await File.ReadAllTextAsync(destinationFile));
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
