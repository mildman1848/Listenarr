namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs;

[Trait("Name", "MoveManifestIdentityTests")]
[Trait("Category", "Application")]
public sealed class MoveManifestIdentityTests
{
    [Fact]
    public void SourceManifestsMatch_NormalizesInsensitivePathsAndIgnoresDirectoryTimestamp()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var current = new MoveSourceManifestEntry[]
        {
            new(
                "Disc/One",
                MoveJobEntryType.Directory,
                0,
                DateTime.UtcNow,
                null),
            new(
                "Disc/One/Book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('a', 64))
        };
        var persisted = new MoveJobEntry[]
        {
            new()
            {
                RelativePath = "disc\\one",
                EntryType = MoveJobEntryType.Directory,
                LastWriteTimeUtc = DateTime.UnixEpoch.AddYears(10)
            },
            new()
            {
                RelativePath = "disc\\one\\book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch,
                Sha256 = new string('A', 64)
            }
        };

        Assert.True(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_FileTimestampChanges_ReturnsFalse()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[]
        {
            new MoveSourceManifestEntry(
                "book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('A', 64))
        };
        var persisted = new[]
        {
            new MoveJobEntry
            {
                RelativePath = "book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch.AddTicks(1),
                Sha256 = new string('A', 64)
            }
        };

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }

    [Fact]
    public void SourceManifestsMatch_DirectorySetChanges_ReturnsFalse()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var current = new[]
        {
            new MoveSourceManifestEntry(
                "CD1/book.m4b",
                MoveJobEntryType.File,
                5,
                DateTime.UnixEpoch,
                new string('A', 64))
        };
        var persisted = new[]
        {
            new MoveJobEntry
            {
                RelativePath = "CD1",
                EntryType = MoveJobEntryType.Directory
            },
            new MoveJobEntry
            {
                RelativePath = "CD1/book.m4b",
                EntryType = MoveJobEntryType.File,
                Length = 5,
                LastWriteTimeUtc = DateTime.UnixEpoch,
                Sha256 = new string('A', 64)
            }
        };

        Assert.False(MoveManifestIdentity.SourceManifestsMatch(
            current,
            persisted,
            semantics));
    }
}
