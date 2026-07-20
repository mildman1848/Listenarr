/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Diagnostics;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryController_DeleteLinkSafetyTests")]
[Trait("Category", "LibraryController")]
public class LibraryController_DeleteLinkSafetyTests : BaseTests
{
    [Fact]
    public async Task FilesystemDelete_LinkedDirectoryDoesNotDeleteExternalFiles()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-link-root");
        var bookFolder = Path.Join(tempRoot, "Book");
        var externalFolder = FileService.GetTempDirectory("listenarr-delete-link-external");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var externalFile = Path.Join(externalFolder, "external.txt");
        var linkedDirectory = Path.Join(bookFolder, "linked");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "audio");
        await File.WriteAllTextAsync(externalFile, "external");

        if (!TryCreateDirectoryLink(linkedDirectory, externalFolder))
        {
            return;
        }

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Linked Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(localFile)
            .Build());

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(File.Exists(externalFile));
        Assert.True(File.Exists(localFile));
        Assert.True(Directory.Exists(bookFolder));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(linkedDirectory, recursive: false);
    }

    [Fact]
    public async Task FilesystemDelete_LinkedFileDoesNotDeleteExternalFile()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-file-link-root");
        var bookFolder = Path.Join(tempRoot, "Book");
        var externalFolder = FileService.GetTempDirectory("listenarr-delete-file-link-external");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var externalFile = Path.Join(externalFolder, "external.txt");
        var linkedFile = Path.Join(bookFolder, "linked.txt");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "audio");
        await File.WriteAllTextAsync(externalFile, "external");

        try
        {
            File.CreateSymbolicLink(linkedFile, externalFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Linked File Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(localFile)
            .Build());

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(File.Exists(externalFile));
        Assert.True(File.Exists(localFile));
        Assert.True(File.Exists(linkedFile));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
