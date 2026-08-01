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
 */
using System.Diagnostics;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Name", "FileMoverPathAliasTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverPathAliasTests : BaseTests, IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        $"listenarr-file-mover-alias-{Guid.NewGuid():N}");

    public FileMoverPathAliasTests()
    {
        Directory.CreateDirectory(_root);
    }

    [DirectoryLinkFact]
    public async Task MoveDirectoryAsync_DestinationSymbolicLinkAlias_IsBlockedBeforeMutation()
    {

        var source = Path.Join(_root, "destination-alias-source");
        var destination = Path.Join(_root, "destination-alias");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
        Directory.CreateSymbolicLink(destination, source);
        var treePreflightCalls = 0;
        var mover = CreateMover(() => treePreflightCalls++);

        var result = await mover.MoveDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal(0, treePreflightCalls);
        Assert.True(Directory.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
    }

    [DirectoryLinkFact]
    public async Task MoveDirectoryAsync_SourceSymbolicLinkAlias_IsBlockedBeforeMutation()
    {

        var destination = Path.Join(_root, "source-alias-target");
        var source = Path.Join(_root, "source-alias");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Join(destination, "book.m4b"), "audio");
        Directory.CreateSymbolicLink(source, destination);
        var treePreflightCalls = 0;
        var mover = CreateMover(() => treePreflightCalls++);

        var result = await mover.MoveDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal(0, treePreflightCalls);
        Assert.True(Directory.Exists(destination));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(destination, "book.m4b")));
    }

    [DirectoryLinkFact]
    public async Task MoveDirectoryAsync_SymbolicLinkAncestorAlias_IsBlockedBeforeMutation()
    {

        var physicalParent = Path.Join(_root, "physical-parent");
        var source = Path.Join(physicalParent, "book");
        var aliasParent = Path.Join(_root, "alias-parent");
        var destination = Path.Join(aliasParent, "book");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
        Directory.CreateSymbolicLink(aliasParent, physicalParent);
        var treePreflightCalls = 0;
        var mover = CreateMover(() => treePreflightCalls++);

        var result = await mover.MoveDirectoryAsync(source, destination);

        Assert.False(result);
        Assert.Equal(0, treePreflightCalls);
        Assert.True(Directory.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
    }

    private static FileMover CreateMover(Action beforeTreePreflight) =>
        new(
            new NullLogger<FileMover>(),
            options: Options.Create(new FileMoverOptions { MaxRetries = 1 }),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            BeforeDirectoryTreePreflightForTest = beforeTreePreflight
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {exception.Message}");
        }
    }
}
