/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Persistence")]
public sealed class RootFolderReassignmentTransactionTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"root-reassignment-{Guid.NewGuid():N}.db");
    private IDbContextFactory<ListenArrDbContext> _factory = null!;
    private EfRootFolderRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False;Foreign Keys=True")
            .Options;
        _factory = new TestDbContextFactory(options);
        _repository = new EfRootFolderRepository(
            _factory,
            NullLogger<EfRootFolderRepository>.Instance);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_RewritesAllReferencesAndDeletesRoot()
    {
        var sourcePath = Path.Join(Path.GetTempPath(), $"root-reassign-source-{Guid.NewGuid():N}");
        var targetPath = Path.Join(Path.GetTempPath(), $"root-reassign-target-{Guid.NewGuid():N}");
        int sourceRootId;
        int targetRootId;
        int audiobookId;
        var sourceBasePath = Path.Join(sourcePath, "Author", "Title");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder { Name = "Source", Path = sourcePath };
            var targetRoot = new RootFolder { Name = "Target", Path = targetPath };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = sourceBasePath,
                FilePath = Path.Join(sourceBasePath, "book.m4b"),
                ImageUrl = Path.Join(sourceBasePath, "cover.jpg"),
                Files =
                [
                    new AudiobookFile { Path = Path.Join(sourceBasePath, "book.m4b") },
                    new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") }
                ]
            };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
            audiobookId = audiobook.Id;
        }

        await _repository.ReassignAudiobooksAndRemoveAsync(
            sourceRootId,
            targetRootId,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemPathSemantics.CurrentHostDefault);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Null(await verification.RootFolders.FindAsync(sourceRootId));
        var updated = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Id == audiobookId);
        var expectedBasePath = Path.Join(targetPath, "Author", "Title");
        Assert.Equal(expectedBasePath, updated.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), updated.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "cover.jpg"), updated.ImageUrl);
        Assert.Contains(updated.Files!, file => file.Path == Path.Join(expectedBasePath, "book.m4b"));
        Assert.Contains(updated.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_CaseVariantsCollapseOnInsensitiveTarget_RollsBack()
    {
        var identity = Guid.NewGuid().ToString("N");
        var sourcePath = $@"C:\root-reassign-case-source-{identity}";
        var targetPath = $@"C:\root-reassign-case-target-{identity}";
        var upperBasePath = $@"{sourcePath}\Book";
        var lowerBasePath = $@"{sourcePath}\book";
        int sourceRootId;
        int targetRootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder
            {
                Name = "Source",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var targetRoot = new RootFolder
            {
                Name = "Target",
                Path = targetPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Upper",
                    BasePath = upperBasePath,
                    Files =
                    [
                        new AudiobookFile
                        {
                            Path = $@"{upperBasePath}\book.m4b"
                        }
                    ]
                },
                new Audiobook
                {
                    Title = "Lower",
                    BasePath = lowerBasePath,
                    Files =
                    [
                        new AudiobookFile
                        {
                            Path = $@"{lowerBasePath}\book.m4b"
                        }
                    ]
                });
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignAudiobooksAndRemoveAsync(
                sourceRootId,
                targetRootId,
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Sensitive),
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive)));

        Assert.Contains("same filesystem identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolders.FindAsync(sourceRootId));
        Assert.NotNull(await verification.RootFolders.FindAsync(targetRootId));
        var audiobooks = await verification.Audiobooks
            .OrderBy(audiobook => audiobook.Title)
            .ToListAsync();
        Assert.Equal(lowerBasePath, audiobooks[0].BasePath);
        Assert.Equal(upperBasePath, audiobooks[1].BasePath);
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_UnrelatedOwnershipWithoutBasePath_BlocksCollision()
    {
        var identity = Guid.NewGuid().ToString("N");
        var sourcePath = $@"C:\root-reassign-owner-source-{identity}";
        var targetPath = $@"C:\root-reassign-owner-target-{identity}";
        var sourceBasePath = $@"{sourcePath}\Book";
        var targetFilePath = $@"{targetPath}\Book\book.m4b";
        int sourceRootId;
        int targetRootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder
            {
                Name = "Source",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var targetRoot = new RootFolder
            {
                Name = "Target",
                Path = targetPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            var movingAudiobook = new Audiobook
            {
                Title = "Moving",
                BasePath = sourceBasePath,
                Files =
                [
                    new AudiobookFile
                    {
                        Path = $@"{sourceBasePath}\book.m4b"
                    }
                ]
            };
            var existingOwner = AudiobookFile.CreateUnresolved(targetFilePath);
            existingOwner.ApplyPathIdentity(
                targetFilePath,
                AudiobookFilePathIdentity.CreateValid(
                    targetFilePath,
                    new FileSystemPathSemantics(
                        FileSystemPathSyntax.Windows,
                        FileSystemCaseSensitivity.Insensitive),
                    FileSystemCaseSensitivityMode.Insensitive,
                    targetPath));
            var unrelatedAudiobook = new Audiobook
            {
                Title = "Unrelated",
                BasePath = null,
                Files = [existingOwner]
            };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.AddRange(movingAudiobook, unrelatedAudiobook);
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignAudiobooksAndRemoveAsync(
                sourceRootId,
                targetRootId,
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Sensitive),
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive)));

        Assert.Contains("same filesystem identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolders.FindAsync(sourceRootId));
        Assert.NotNull(await verification.RootFolders.FindAsync(targetRootId));
        Assert.Equal(
            sourceBasePath,
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Moving")).BasePath);
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_DeleteConflictRollsBackPathRewrites()
    {
        var sourcePath = Path.Join(Path.GetTempPath(), $"root-reassign-rollback-source-{Guid.NewGuid():N}");
        var targetPath = Path.Join(Path.GetTempPath(), $"root-reassign-rollback-target-{Guid.NewGuid():N}");
        int sourceRootId;
        int targetRootId;
        int audiobookId;
        var sourceBasePath = Path.Join(sourcePath, "Author", "Title");
        var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder { Name = "Source", Path = sourcePath };
            var targetRoot = new RootFolder { Name = "Target", Path = targetPath };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = sourceBasePath,
                FilePath = sourceFilePath,
                Files = [new AudiobookFile { Path = sourceFilePath }]
            };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var triggerSql =
                """
                CREATE TRIGGER prevent_root_reassignment_delete
                BEFORE DELETE ON RootFolders
                WHEN OLD.Id =
                """
                + sourceRoot.Id
                + """

                BEGIN
                    SELECT RAISE(ABORT, 'forced root delete failure');
                END;
                """;
            await db.Database.ExecuteSqlRawAsync(triggerSql);
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
            audiobookId = audiobook.Id;
        }

        await Assert.ThrowsAsync<Listenarr.Application.Common.PersistenceException>(() =>
            _repository.ReassignAudiobooksAndRemoveAsync(
                sourceRootId,
                targetRootId,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolders.FindAsync(sourceRootId));
        var unchanged = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Id == audiobookId);
        Assert.Equal(sourceBasePath, unchanged.BasePath);
        Assert.Equal(sourceFilePath, unchanged.FilePath);
        Assert.Equal(sourceFilePath, Assert.Single(unchanged.Files!).Path);
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_RootEqualReferencesRewriteAndRelativeReferencesRemain()
    {
        var sourcePath = Path.Join(Path.GetTempPath(), $"root-reassign-equal-source-{Guid.NewGuid():N}");
        var targetPath = Path.Join(Path.GetTempPath(), $"root-reassign-equal-target-{Guid.NewGuid():N}");
        int sourceRootId;
        int targetRootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder { Name = "Source", Path = sourcePath };
            var targetRoot = new RootFolder { Name = "Target", Path = targetPath };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = sourcePath,
                FilePath = sourcePath,
                ImageUrl = "https://example.com/cover.jpg",
                Files = [new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") }]
            };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
            audiobookId = audiobook.Id;
        }

        await _repository.ReassignAudiobooksAndRemoveAsync(
            sourceRootId,
            targetRootId,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemPathSemantics.CurrentHostDefault);

        await using var verification = await _factory.CreateDbContextAsync();
        var updated = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Id == audiobookId);
        Assert.Equal(targetPath, updated.BasePath);
        Assert.Equal(targetPath, updated.FilePath);
        Assert.Equal("https://example.com/cover.jpg", updated.ImageUrl);
        Assert.Equal(Path.Join("disc-1", "chapter.mp3"), Assert.Single(updated.Files!).Path);
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_ActiveMoveInsideTransactionBlocksAllChanges()
    {
        var sourcePath = Path.Join(Path.GetTempPath(), $"root-reassign-active-source-{Guid.NewGuid():N}");
        var targetPath = Path.Join(Path.GetTempPath(), $"root-reassign-active-target-{Guid.NewGuid():N}");
        var sourceBasePath = Path.Join(sourcePath, "Book");
        int sourceRootId;
        int targetRootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder { Name = "Source", Path = sourcePath };
            var targetRoot = new RootFolder { Name = "Target", Path = targetPath };
            var audiobook = new Audiobook { Title = "Book", BasePath = sourceBasePath };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = sourceBasePath,
                RequestedPath = Path.Join(targetPath, "Book"),
                Status = MoveJobStatus.Running,
                ActiveDeduplicationKey = $"test:{Guid.NewGuid():N}"
            });
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
            audiobookId = audiobook.Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignAudiobooksAndRemoveAsync(
                sourceRootId,
                targetRootId,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolders.FindAsync(sourceRootId));
        Assert.Equal(sourceBasePath, (await verification.Audiobooks.FindAsync(audiobookId))!.BasePath);
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_ActiveRelocationInsideTransactionBlocksAllChanges()
    {
        var sourcePath = Path.Join(Path.GetTempPath(), $"root-reassign-relocation-source-{Guid.NewGuid():N}");
        var targetPath = Path.Join(Path.GetTempPath(), $"root-reassign-relocation-target-{Guid.NewGuid():N}");
        var sourceBasePath = Path.Join(sourcePath, "Book");
        int sourceRootId;
        int targetRootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var sourceRoot = new RootFolder { Name = "Source", Path = sourcePath };
            var targetRoot = new RootFolder { Name = "Target", Path = targetPath };
            var audiobook = new Audiobook { Title = "Book", BasePath = sourceBasePath };
            db.RootFolders.AddRange(sourceRoot, targetRoot);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = sourceRoot.Id,
                ActiveRootFolderId = sourceRoot.Id,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                DesiredName = "Source",
                Status = RootFolderRelocationStatus.Running
            });
            await db.SaveChangesAsync();
            sourceRootId = sourceRoot.Id;
            targetRootId = targetRoot.Id;
            audiobookId = audiobook.Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignAudiobooksAndRemoveAsync(
                sourceRootId,
                targetRootId,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolders.FindAsync(sourceRootId));
        Assert.Equal(sourceBasePath, (await verification.Audiobooks.FindAsync(audiobookId))!.BasePath);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
