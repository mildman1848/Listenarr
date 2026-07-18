using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MoveSourceManifestServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveSourceManifestServiceTests : BaseTests
{
    [Fact]
    public async Task BuildAsync_BroadAuthorBasePath_UsesTrackedBookDirectory()
    {
        var root = FileService.GetTempDirectory("move-manifest-root");
        var author = Path.Join(root, "Shared Author");
        var requestedBook = Path.Join(author, "Book One");
        var siblingBook = Path.Join(author, "Book Two");
        Directory.CreateDirectory(requestedBook);
        Directory.CreateDirectory(siblingBook);
        var requestedFile = await FileService.GetFileAsync(
            requestedBook,
            "Book One.m4b",
            "requested");
        _ = await FileService.GetFileAsync(
            siblingBook,
            "Book Two.m4b",
            "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book One")
                .WithBasePath(author)
                .Build());
        await AddTrackedFileAsync(audiobook, requestedFile, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(requestedBook, manifest.SourceRoot);
        var file = Assert.Single(
            manifest.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        Assert.Equal("Book One.m4b", file.RelativePath);
        Assert.DoesNotContain(manifest.Entries, entry =>
            entry.RelativePath.Contains("Book Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_SharedFlatFolder_IncludesOnlyTrackedFile()
    {
        var root = FileService.GetTempDirectory("move-manifest-flat");
        var requestedFile = await FileService.GetFileAsync(
            root,
            "Book One.m4b",
            "requested");
        _ = await FileService.GetFileAsync(
            root,
            "Book Two.m4b",
            "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book One")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, requestedFile, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(root, manifest.SourceRoot);
        var file = Assert.Single(manifest.Entries);
        Assert.Equal("Book One.m4b", file.RelativePath);
        Assert.Equal(MoveJobEntryType.File, file.EntryType);
    }

    [Fact]
    public async Task BuildAsync_NestedDiscs_UsesCommonBookDirectory()
    {
        var root = FileService.GetTempDirectory("move-manifest-discs");
        var book = Path.Join(root, "Author", "Book");
        var firstDirectory = Path.Join(book, "CD1");
        var secondDirectory = Path.Join(book, "CD2");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = await FileService.GetFileAsync(firstDirectory, "01.mp3", "one");
        var second = await FileService.GetFileAsync(secondDirectory, "02.mp3", "two");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, first, root);
        await AddTrackedFileAsync(audiobook, second, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(book, manifest.SourceRoot);
        Assert.Equal(
            ["CD1/01.mp3", "CD2/02.mp3"],
            manifest.Entries
                .Where(entry => entry.EntryType == MoveJobEntryType.File)
                .Select(entry => entry.RelativePath.Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(2, manifest.Entries.Count(entry =>
            entry.EntryType == MoveJobEntryType.Directory));
    }

    [Fact]
    public async Task BuildAsync_UnrelatedTrackedDirectories_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-unrelated");
        var firstDirectory = Path.Join(root, "Book One");
        var secondDirectory = Path.Join(root, "Book Two");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = await FileService.GetFileAsync(firstDirectory, "01.mp3", "one");
        var second = await FileService.GetFileAsync(secondDirectory, "02.mp3", "two");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Ambiguous")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, first, root);
        await AddTrackedFileAsync(audiobook, second, root);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("unrelated source directories", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_NoTrackedFiles_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-empty");
        _ = await FileService.GetFileAsync(root, "Untracked.m4b", "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Untracked")
                .WithBasePath(root)
                .Build());

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("no validated tracked files", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_MissingTrackedFile_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-missing");
        var missing = Path.Join(root, "Missing.m4b");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Missing")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, missing, root);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("missing from disk", exception.Message);
    }

    private async Task AddTrackedFileAsync(
        Audiobook audiobook,
        string path,
        string boundary)
    {
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = AudiobookFilePathIdentity.CreateValid(
            path,
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            boundary);
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(path)
            .Build();
        tracked.ApplyPathIdentity(path, identity);
        await _audiobookFileRepository.AddAsync(tracked);
    }
}
