/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "ScanFileDiscoveryTests")]
[Trait("Category", "Infrastructure")]
public sealed class ScanFileDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        $"listenarr-scan-discovery-{Guid.NewGuid():N}");

    public ScanFileDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FindMatchingAudioFiles_SameAuthorSiblingBooks_ReturnsOnlyRequestedBook()
    {
        var requested = CreateAudioFile("Shared Author", "Book One", "Book One.m4b");
        _ = CreateAudioFile("Shared Author", "Book Two", "Book Two.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book One")
            .WithAuthor("Shared Author")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_SameTitleDifferentAuthors_ReturnsOnlyRequestedAuthor()
    {
        var requested = CreateAudioFile("Author One", "Shared Title", "Shared Title.m4b");
        _ = CreateAudioFile("Author Two", "Shared Title", "Shared Title.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Shared Title")
            .WithAuthor("Author One")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_ShortTitle_DoesNotMatchSubstringInUnrelatedFilename()
    {
        var requested = CreateAudioFile("Author", "It", "It.m4b");
        _ = CreateAudioFile("Other Author", "Little Women", "Little Women.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("It")
            .WithAuthor("Author")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_NestedDiscDirectories_ReturnsAllFilesBelowBookBoundary()
    {
        var first = CreateAudioFile("Author", "Book", "CD1", "01.mp3");
        var second = CreateAudioFile("Author", "Book", "CD2", "02.mp3");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(
            [first, second],
            result.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    private List<string> Discover(Audiobook audiobook) =>
        ScanFileDiscovery.Discover(
            new LocalFileSystem(),
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault)
        .AttributedFiles
        .ToList();

    private string CreateAudioFile(params string[] segments)
    {
        var path = Path.Join([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary test data.
        }
    }
}
