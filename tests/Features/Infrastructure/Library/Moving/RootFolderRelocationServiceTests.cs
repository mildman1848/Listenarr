using Listenarr.Tests.Mocks;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed class RootFolderRelocationServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"relocation-{Guid.NewGuid():N}.db");
    private string TempRoot => Path.GetDirectoryName(_databasePath)!;
    private TestDbContextFactory _factory = null!;
    private readonly AudiobookOperationCoordinator _operationCoordinator = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        _operationCoordinator.Dispose();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartRelocation_PersistsSagaAndJobsWithoutChangingRootOrAudiobooks()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Author", "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        Assert.True(FileSystemPathIdentity.IsSameOrInside(
            Path.Join(source, "Author", "Title"),
            source,
            FileSystemPathSemantics.CurrentHostDefault));
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var job = await verification.MoveJobs.SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(Path.Join(source, "Author", "Title"), audiobookAfter.BasePath);
        Assert.Equal(rootId, relocation.ActiveRootFolderId);
        Assert.Equal(relocation.Id, job.RelocationId);
        Assert.Equal(source, job.SourceCleanupBoundary);
        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
        Assert.True(await service.IsBoundaryProtectedAsync(
            target,
            FileSystemPathSemantics.CurrentHostDefault));
        Assert.True(await service.IsBoundaryProtectedAsync(
            source,
            FileSystemPathSemantics.CurrentHostDefault));
    }

    [Fact]
    public async Task StartRelocation_IdenticalSourceAndTarget_RejectsBeforePersistingChildJobs()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-identical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Author", "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    source,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnlyPathChange_RepairsInvalidStoredRootPath()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var unrelatedBasePath = Path.Join(Path.GetTempPath(), $"unrelated-repair-book-{Guid.NewGuid():N}");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Stale",
                Path = "relative-root",
                PathIdentityState = PathIdentityState.Unavailable,
                PathIdentityKey = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Unrelated",
                BasePath = unrelatedBasePath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired",
                true,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var repaired = await verification.RootFolders.SingleAsync();
        Assert.Equal(target, repaired.Path);
        Assert.Equal("Repaired", repaired.Name);
        Assert.Equal(PathIdentityState.Valid, repaired.PathIdentityState);
        Assert.NotNull(repaired.PathIdentityKey);
        Assert.Equal(unrelatedBasePath, (await verification.Audiobooks.SingleAsync()).BasePath);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_ForeignSyntaxRoot_RewritesRawStoredAudiobookPaths()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-foreign-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var sourceRoot = OperatingSystem.IsWindows() ? "/legacy/library" : @"Z:\legacy\library";
        var sourceBook = OperatingSystem.IsWindows()
            ? sourceRoot + "/Author/Title"
            : sourceRoot + @"\Author\Title";
        var sourceFile = OperatingSystem.IsWindows()
            ? sourceBook + "/book.m4b"
            : sourceBook + @"\book.m4b";
        var unrelatedBasePath = Path.Join(Path.GetTempPath(), $"unrelated-book-{Guid.NewGuid():N}");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Foreign",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Affected",
                    BasePath = sourceBook,
                    FilePath = sourceFile,
                    Files = [new AudiobookFile { Path = sourceFile }]
                },
                new Audiobook
                {
                    Title = "Unrelated",
                    BasePath = unrelatedBasePath
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Foreign Root",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var affected = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Title == "Affected");
        var expectedBasePath = Path.Join(target, "Author", "Title");
        Assert.Equal(expectedBasePath, affected.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), affected.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), Assert.Single(affected.Files!).Path);
        Assert.Equal(
            unrelatedBasePath,
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Unrelated")).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [Fact]
    public async Task RelocatePathChange_RejectsInvalidStoredRootPath()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Stale", Path = "relative-root" };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    false,
                    "Still Stale",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Contains("metadata-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileActive_MarksInvalidStoredRootUnavailableInsteadOfThrowing()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.RootFolders.Add(new RootFolder
            {
                Name = "Stale",
                Path = "relative-root",
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = "stale"
            });
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
        Assert.Null(root.PathIdentityKey);
        Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
    }

    [Fact]
    public async Task ConcurrentMoveFirst_BlocksWaitingRelocationAfterMoveIsPersisted()
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var relocationService = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var moveService = new MoveQueueService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MoveQueueService>.Instance,
            new EfMoveQueuePersistence(_factory, new FileSystemSemanticsResolver()),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FileSystemSemanticsResolver(),
            relocationService,
            coordinator);
        var standaloneTarget = Path.Join(Path.GetTempPath(), $"standalone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(standaloneTarget);

        var moveTask = moveService.EnqueueMoveAsync(
            audiobookId,
            standaloneTarget,
            Path.Join(source, "Author", "Title"));
        await coordinator.FirstEntered;
        var relocationTask = relocationService.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await Task.Delay(50);
        Assert.False(relocationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await moveTask;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => relocationTask);
        Assert.Contains("active move job", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentRelocationFirst_BlocksWaitingMoveAfterRelocationIsPersisted()
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var relocationService = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var moveService = new MoveQueueService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MoveQueueService>.Instance,
            new EfMoveQueuePersistence(_factory, new FileSystemSemanticsResolver()),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FileSystemSemanticsResolver(),
            relocationService,
            coordinator);

        var relocationTask = relocationService.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await coordinator.FirstEntered;
        var moveTask = moveService.EnqueueMoveAsync(
            audiobookId,
            Path.Join(target, "Author", "Title"),
            Path.Join(source, "Author", "Title"));
        await Task.Delay(50);
        Assert.False(moveTask.IsCompleted);

        coordinator.ReleaseFirst();
        await relocationTask;
        await Assert.ThrowsAsync<MoveRelocationConflictException>(() => moveTask);
    }

    [Fact]
    public async Task RelocationStart_WaitsForActiveAudiobookOperationBeforeLoadingTransactionState()
    {
        var (rootId, audiobookId, _, target) = await SeedRelocationScenarioAsync();
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = _operationCoordinator.ExecuteExclusiveAsync(
            audiobookId,
            async _ =>
            {
                operationEntered.SetResult();
                await releaseOperation.Task;
            });
        await operationEntered.Task;

        var relocationTask = CreateService().StartAsync(
            rootId,
            BuildRelocationCommand(target));

        await Task.Delay(50);
        Assert.False(relocationTask.IsCompleted);
        releaseOperation.SetResult();
        await blocker;

        var result = await relocationTask;
        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
    }

    [Fact]
    public async Task MetadataOnly_UpdatesRootAndAudiobooksInOneTransaction()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-target-{Guid.NewGuid():N}");
        var unrelated = Path.Join(Path.GetTempPath(), $"metadata-unrelated-{Guid.NewGuid():N}", "bonus.mp3");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var localBasePath = Path.Join(source, "Title");
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Title",
                    BasePath = localBasePath,
                    FilePath = Path.Join(localBasePath, "book.m4b"),
                    ImageUrl = Path.Join(localBasePath, "cover.jpg"),
                    Files =
                    [
                        new AudiobookFile { Path = Path.Join(localBasePath, "book.m4b") },
                        new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") },
                        new AudiobookFile { Path = unrelated }
                    ]
                },
                new Audiobook
                {
                    Title = "Remote Image",
                    BasePath = Path.Join(source, "Remote Image"),
                    ImageUrl = "https://example.test/cover.jpg"
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Metadata Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var audiobooks = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .OrderBy(audiobook => audiobook.Title)
            .ToListAsync();
        var remoteImageAudiobook = audiobooks[0];
        var localAudiobook = audiobooks[1];
        var expectedBasePath = Path.Join(target, "Title");
        Assert.Equal(expectedBasePath, localAudiobook.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), localAudiobook.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "cover.jpg"), localAudiobook.ImageUrl);
        Assert.Contains(localAudiobook.Files!, file => file.Path == Path.Join(expectedBasePath, "book.m4b"));
        Assert.Contains(localAudiobook.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
        Assert.Contains(localAudiobook.Files!, file => file.Path == unrelated);
        Assert.Equal(Path.Join(target, "Remote Image"), remoteImageAudiobook.BasePath);
        Assert.Equal("https://example.test/cover.jpg", remoteImageAudiobook.ImageUrl);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task MetadataOnly_InvalidStoredAudiobookBasePathIsSkippedWithoutAbortingOtherUpdates()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-invalid-base-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-invalid-base-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        int invalidAudiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var validBasePath = Path.Join(source, "Valid");
            var invalid = new Audiobook
            {
                Title = "Invalid Legacy Path",
                BasePath = "\0invalid"
            };
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Valid",
                    BasePath = validBasePath,
                    FilePath = Path.Join(validBasePath, "book.m4b")
                },
                invalid);
            await db.SaveChangesAsync();
            rootId = root.Id;
            invalidAudiobookId = invalid.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Equal(2, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        var audiobooks = await verification.Audiobooks.OrderBy(audiobook => audiobook.Title).ToListAsync();
        Assert.Equal("\0invalid", audiobooks[0].BasePath);
        Assert.Equal(Path.Join(target, "Valid"), audiobooks[1].BasePath);
        Assert.Equal(Path.Join(target, "Valid", "book.m4b"), audiobooks[1].FilePath);

        var relocation = await verification.RootFolderRelocations
            .Include(candidate => candidate.SkippedItems)
            .SingleAsync();
        var skipped = Assert.Single(relocation.SkippedItems);
        Assert.Equal(invalidAudiobookId, skipped.AudiobookId);
        Assert.Contains("invalid", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_SourceRootFilePathReferencesAreRewrittenWithoutAttentionRecord()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-root-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-source-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var firstBasePath = Path.Join(source, "A Valid");
            var sourceRootFilePath = Path.Join(source, "M Source Root");
            var lastBasePath = Path.Join(source, "Z Valid");
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "A Valid",
                    BasePath = firstBasePath,
                    FilePath = Path.Join(firstBasePath, "book.m4b"),
                    ImageUrl = Path.Join(firstBasePath, "cover.jpg")
                },
                new Audiobook
                {
                    Title = "M Source Root",
                    BasePath = sourceRootFilePath,
                    FilePath = sourceRootFilePath,
                    ImageUrl = Path.Join(sourceRootFilePath, "cover.jpg")
                },
                new Audiobook
                {
                    Title = "Z Valid",
                    BasePath = lastBasePath,
                    FilePath = Path.Join(lastBasePath, "book.m4b"),
                    ImageUrl = Path.Join(lastBasePath, "cover.jpg")
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(3, result.TotalJobs);
        Assert.Equal(3, result.CompletedJobs);
        Assert.Null(result.RelocationId);

        var audiobooks = await verification.Audiobooks.OrderBy(audiobook => audiobook.Title).ToListAsync();
        Assert.Equal(Path.Join(target, "A Valid"), audiobooks[0].BasePath);
        Assert.Equal(Path.Join(target, "A Valid", "book.m4b"), audiobooks[0].FilePath);
        Assert.Equal(Path.Join(target, "A Valid", "cover.jpg"), audiobooks[0].ImageUrl);
        Assert.Equal(Path.Join(target, "M Source Root"), audiobooks[1].BasePath);
        Assert.Equal(Path.Join(target, "M Source Root"), audiobooks[1].FilePath);
        Assert.Equal(Path.Join(target, "M Source Root", "cover.jpg"), audiobooks[1].ImageUrl);
        Assert.Equal(Path.Join(target, "Z Valid"), audiobooks[2].BasePath);
        Assert.Equal(Path.Join(target, "Z Valid", "book.m4b"), audiobooks[2].FilePath);
        Assert.Equal(Path.Join(target, "Z Valid", "cover.jpg"), audiobooks[2].ImageUrl);

        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_SourceRootFilePathCompletesWithoutRetry()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-root-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-source-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var basePath = Path.Join(source, "Title");
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = basePath,
                FilePath = basePath,
                ImageUrl = Path.Join(basePath, "cover.jpg")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var started = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, started.Status);
        Assert.Equal(1, started.CompletedJobs);
        Assert.Null(started.RelocationId);

        await using var verification = await _factory.CreateDbContextAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.BasePath);
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.FilePath);
        Assert.Equal(Path.Join(target, "Title", "cover.jpg"), audiobookAfter.ImageUrl);
    }

    [Fact]
    public async Task ConcurrentRetryAsync_SerializesStateTransitions()
    {
        var (relocationId, rootId) = await SeedRetryableRelocationAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);

        var firstRetry = service.RetryAsync(relocationId);
        await coordinator.FirstEntered;
        var secondRetry = service.RetryAsync(relocationId);

        await Task.Delay(50);
        Assert.Equal(1, coordinator.EntryCount);

        coordinator.ReleaseFirst();
        var firstResult = await firstRetry;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondRetry);

        Assert.Equal(RootFolderRelocationStatus.Completed, firstResult.Status);
        Assert.Contains("needing attention", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, coordinator.EntryCount);
        await using var verification = await _factory.CreateDbContextAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(relocationId, relocation.Id);
        Assert.Equal(rootId, root.Id);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [Fact]
    public async Task RetryAsync_BroadcastsAfterReleasingCoordinator()
    {
        var (relocationId, _) = await SeedRetryableRelocationAsync();
        var coordinator = new TrackingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => coordinator.IsExecuting);
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator);

        var result = await service.RetryAsync(relocationId);

        Assert.Equal(1, broadcaster.BroadcastCount);
        Assert.False(broadcaster.CoordinatorWasExecuting);
        Assert.Same(result, broadcaster.Payload);
    }

    [Fact]
    public async Task RetryAsync_CancelledWhileWaiting_DoesNotMutateOrBroadcast()
    {
        var (relocationId, rootId) = await SeedRetryableRelocationAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => false);
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var blocker = coordinator.ExecuteExclusiveAsync(_ => Task.CompletedTask);
        await coordinator.FirstEntered;
        using var cancellation = new CancellationTokenSource();

        var retry = service.RetryAsync(relocationId, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Equal(0, broadcaster.BroadcastCount);
        await using (var verification = await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations.SingleAsync();
            var root = await verification.RootFolders.SingleAsync();
            Assert.Equal(rootId, relocation.ActiveRootFolderId);
            Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
            Assert.Equal(rootId, root.Id);
        }

        coordinator.ReleaseFirst();
        await blocker;
    }

    [Fact]
    public async Task RetryAsync_AllJobsCompletedAfterFinalizationBlocked_AppliesRootMetadataAndCompletes()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-finalize-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-finalize-target-{Guid.NewGuid():N}");
        var otherRootPath = Path.Join(Path.GetTempPath(), $"retry-finalize-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Other",
                Path = otherRootPath,
                IsDefault = true,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Insensitive));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var otherRootAfter = await verification.RootFolders.SingleAsync(root => root.Id != rootId);
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Moved Library", rootAfter.Name);
        Assert.True(rootAfter.IsDefault);
        Assert.False(otherRootAfter.IsDefault);
        Assert.Equal(FileSystemCaseSensitivityMode.Insensitive, rootAfter.CaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivity.Insensitive, rootAfter.ResolvedCaseSensitivity);
        Assert.Null(relocationAfter.ActiveRootFolderId);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Equal(relocationAfter.TotalJobs, relocationAfter.CompletedJobs);
    }

    [Fact]
    public async Task RetryAsync_AllJobsCompletedButTargetStillUnavailable_StaysNeedsAttentionWithoutMutatingRoot()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-finalize-unavailable-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-finalize-unavailable-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Insensitive));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            await db.SaveChangesAsync();
        }

        var coordinator = new TrackingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => coordinator.IsExecuting);
        var retryService = new RootFolderRelocationService(
            _factory,
            new TargetUnavailableSemanticsResolver(target),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var result = await retryService.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("became unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, broadcaster.BroadcastCount);
        Assert.False(broadcaster.CoordinatorWasExecuting);
        Assert.Same(result, broadcaster.Payload);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal("Library", rootAfter.Name);
        Assert.False(rootAfter.IsDefault);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, rootAfter.CaseSensitivityMode);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.Equal(rootId, relocationAfter.ActiveRootFolderId);
    }

    [Fact]
    public async Task StartRelocation_RejectsTargetWithCurrentDirectorySegment()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-current-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-current-target-{Guid.NewGuid():N}", ".");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto)));
        Assert.Contains("current directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsTargetWithParentTraversalSegment()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-parent-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-parent-target-{Guid.NewGuid():N}", "Child", "..", "Other");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto)));
        Assert.Contains("parent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_AllowsOrdinaryValidTargetPath()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-valid-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-valid-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
    }

    [Fact]
    public async Task StartRelocation_RejectsCaseOnlyTargetConflictWithInsensitiveExistingRoot()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-case-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-case-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive)));
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsNestedTargetConflictWithInsensitiveExistingRoot()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-nested-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-nested-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books", "Child");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive)));
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsCaseOnlyTargetConflictWhenTargetIsInsensitive()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-reverse-case-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-reverse-case-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive)));
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_NonRelocationJob_SkipsGlobalCoordinator()
    {
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = new Audiobook
            {
                Title = "Ordinary Move",
                BasePath = Path.Join(Path.GetTempPath(), $"ordinary-move-{Guid.NewGuid():N}")
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var job = new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = audiobook.BasePath,
                RequestedPath = Path.Join(Path.GetTempPath(), $"ordinary-target-{Guid.NewGuid():N}"),
                Status = MoveJobStatus.Running
            };
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);

        await service.OnMoveJobStateChangedAsync(jobId);

        Assert.Equal(0, coordinator.EntryCount);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_NonTerminalRelocationJob_SkipsGlobalCoordinator()
    {
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = Path.Join(Path.GetTempPath(), $"running-relocation-source-{Guid.NewGuid():N}");
            var target = Path.Join(Path.GetTempPath(), $"running-relocation-target-{Guid.NewGuid():N}");
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Running", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                TargetPath = target,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.Running,
                DesiredName = root.Name,
                TotalJobs = 1
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            var job = new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = audiobook.BasePath,
                RequestedPath = Path.Join(target, "Title"),
                Status = MoveJobStatus.Running,
                RelocationId = relocation.Id
            };
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);

        await service.OnMoveJobStateChangedAsync(jobId);

        Assert.Equal(0, coordinator.EntryCount);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_WaitsForFilesystemMutationCoordinator()
    {
        var source = Path.Join(Path.GetTempPath(), $"finalize-lock-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"finalize-lock-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Finalized Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var audiobook = await db.Audiobooks.SingleAsync();
            audiobook.BasePath = job.RequestedPath;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var finalizationTask = service.OnMoveJobStateChangedAsync(jobId);
        await coordinator.FirstEntered;
        Assert.False(finalizationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await finalizationTask;

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            RootFolderRelocationStatus.Completed,
            (await verification.RootFolderRelocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReconcileActive_WaitsForFilesystemMutationCoordinator()
    {
        var source = Path.Join(Path.GetTempPath(), $"reconcile-lock-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"reconcile-lock-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Superseded;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator);
        var reconciliationTask = service.ReconcileActiveAsync();
        await coordinator.FirstEntered;
        Assert.False(reconciliationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await reconciliationTask;

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            RootFolderRelocationStatus.NeedsAttention,
            (await verification.RootFolderRelocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompletedJobs_FinalizeRootOnlyAfterEveryAudiobookPathMoved()
    {
        var source = Path.Join(Path.GetTempPath(), $"finalize-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"finalize-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Finalized Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var audiobook = await db.Audiobooks.SingleAsync();
            audiobook.BasePath = job.RequestedPath;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await service.OnMoveJobStateChangedAsync(jobId);

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Finalized Library", rootAfter.Name);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Null(relocationAfter.ActiveRootFolderId);
    }

    [Fact]
    public async Task SupersededJob_RetryPreservesTerminalStaleState()
    {
        var source = Path.Join(Path.GetTempPath(), $"superseded-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"superseded-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Superseded;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await service.ReconcileActiveAsync();
        var needsAttention = await service.GetAsync(started.RelocationId!.Value);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, needsAttention!.Status);

        var retried = await service.RetryAsync(started.RelocationId.Value);
        await using var verification = await _factory.CreateDbContextAsync();
        var preservedJob = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.Superseded, preservedJob.Status);
        Assert.Equal(jobId, preservedJob.Id);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, retried.Status);
        Assert.Contains("superseded", retried.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRootFolder_PreservesCompletedRelocationHistoryAndKeepsHistoryQueryable()
    {
        var source = Path.Join(Path.GetTempPath(), $"delete-root-history-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"delete-root-history-target-{Guid.NewGuid():N}");
        Guid relocationId;
        DateTime? completedAt;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();

            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = null,
                SourcePath = source,
                TargetPath = target,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.Completed,
                DesiredName = "Library",
                TotalJobs = 1,
                CompletedJobs = 1,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                CompletedAt = DateTime.UtcNow
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
            completedAt = relocation.CompletedAt;

            db.RootFolders.Remove(root);
            await db.SaveChangesAsync();
        }

        await using (var verification = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await verification.RootFolders.ToListAsync());
            var relocation = await verification.RootFolderRelocations.SingleAsync(candidate => candidate.Id == relocationId);
            Assert.Null(relocation.RootFolderId);
            Assert.Equal(source, relocation.SourcePath);
            Assert.Equal(target, relocation.TargetPath);
            Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
            Assert.Equal(completedAt, relocation.CompletedAt);
        }

        var result = await CreateService().GetAsync(relocationId);
        Assert.NotNull(result);
        Assert.Null(result!.RootFolderId);
        Assert.Equal(target, result.CurrentPath);
        Assert.Equal(target, result.TargetPath);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RetryAsync_SupersededJobWithCanonicalReplacement_DoesNotCollide()
    {
        var source = Path.Join(Path.GetTempPath(), $"superseded-collision-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"superseded-collision-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var superseded = await db.MoveJobs.SingleAsync();
            var key = superseded.ActiveDeduplicationKey;
            superseded.Status = MoveJobStatus.Superseded;
            superseded.ActiveDeduplicationKey = null;
            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = superseded.AudiobookId,
                RequestedPath = superseded.RequestedPath,
                Status = MoveJobStatus.Running,
                ActiveDeduplicationKey = key
            });
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("were superseded by a newer move", result.Error);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            MoveJobStatus.Superseded,
            (await verification.MoveJobs.SingleAsync(job => job.RelocationId != null)).Status);
        Assert.Equal(
            MoveJobStatus.Running,
            (await verification.MoveJobs.SingleAsync(job => job.RelocationId == null)).Status);
    }

    [Fact]
    public async Task StartRelocation_BroadcastFailureDoesNotUndoCommittedSaga()
    {
        var source = Path.Join(Path.GetTempPath(), $"broadcast-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"broadcast-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new ThrowingHubBroadcaster(),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator);
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.NotNull(result.RelocationId);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.RootFolderRelocations.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
    }

    [Theory]
    [InlineData("audiobook")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("requested-source")]
    [InlineData("source-target")]
    public async Task StartRelocation_RejectsOverlappingActiveStandaloneMove(string conflictKind)
    {
        var source = Path.Join(Path.GetTempPath(), $"active-move-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"active-move-target-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(audiobookPath);
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            var audiobook = new Audiobook { Title = "Title", BasePath = audiobookPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            audiobookId = audiobook.Id;

            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = conflictKind == "audiobook" ? audiobookId : audiobookId + 1000,
                SourcePath = conflictKind switch
                {
                    "source" => Path.Join(source.ToUpperInvariant(), "OTHER"),
                    "source-target" => Path.Join(target.ToUpperInvariant(), "OTHER"),
                    _ => Path.Join(Path.GetTempPath(), $"unrelated-source-{Guid.NewGuid():N}")
                },
                RequestedPath = conflictKind switch
                {
                    "target" => Path.Join(target.ToUpperInvariant(), "OTHER"),
                    "requested-source" => Path.Join(source.ToUpperInvariant(), "OTHER"),
                    _ => Path.Join(Path.GetTempPath(), $"unrelated-target-{Guid.NewGuid():N}")
                },
                Status = MoveJobStatus.Queued,
                EnqueuedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Renamed Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Contains("active move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(audiobookPath, (await verification.Audiobooks.SingleAsync()).BasePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsBoundaryProtectedAsync_HonorsPersistedInsensitiveBoundaryMode(bool useSourceBoundary)
    {
        var protectedPath = Path.Join(TempRoot, useSourceBoundary ? "SourceBoundary" : "TargetBoundary");
        var otherPath = Path.Join(TempRoot, useSourceBoundary ? "TargetBoundary" : "SourceBoundary");
        await SeedActiveRelocationAsync(
            useSourceBoundary ? protectedPath : otherPath,
            useSourceBoundary ? otherPath : protectedPath,
            FileSystemCaseSensitivityMode.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive);
        var service = CreateService();
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.True(protectedResult);
    }

    [Fact]
    public async Task IsBoundaryProtectedAsync_PreservesCaseDistinctSensitiveBoundary()
    {
        var protectedPath = Path.Join(TempRoot, "CaseSensitiveBoundary");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive);
        var service = CreateService();
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.False(protectedResult);
    }

    [Fact]
    public async Task IsBoundaryProtectedAsync_FailsClosedWhenBoundarySemanticsAreUnavailable()
    {
        var protectedPath = Path.Join(TempRoot, "UnavailableBoundary");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Auto,
            FileSystemCaseSensitivityMode.Sensitive);
        var service = new RootFolderRelocationService(
            _factory,
            new TargetUnavailableSemanticsResolver(protectedPath),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator);
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.True(protectedResult);
    }

    [Theory]
    [InlineData("child")]
    [InlineData("parent")]
    public async Task IsBoundaryProtectedAsync_BlocksContainmentInEitherDirection(string relationship)
    {
        var protectedPath = Path.Join(TempRoot, "Boundary", "Nested");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive);
        var candidate = relationship == "child"
            ? Path.Join(protectedPath, "Book")
            : Path.GetDirectoryName(protectedPath)!;

        Assert.True(await CreateService().IsBoundaryProtectedAsync(
            candidate,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive)));
    }

    private async Task SeedActiveRelocationAsync(
        string sourcePath,
        string targetPath,
        FileSystemCaseSensitivityMode sourceMode,
        FileSystemCaseSensitivityMode targetMode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = $"Root-{Guid.NewGuid():N}", Path = sourcePath };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();
        db.RootFolderRelocations.Add(new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = sourcePath,
            SourceCaseSensitivityMode = sourceMode,
            TargetPath = targetPath,
            TargetCaseSensitivityMode = targetMode,
            DesiredName = root.Name,
            Status = RootFolderRelocationStatus.Running
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int RootId, int AudiobookId, string Source, string Target)>
        SeedRelocationScenarioAsync()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        Directory.CreateDirectory(target);
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = "Library", Path = source };
        var audiobook = new Audiobook
        {
            Title = "Title",
            BasePath = Path.Join(source, "Author", "Title")
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        return (root.Id, audiobook.Id, source, target);
    }

    private static RootFolderPathChangeCommand BuildRelocationCommand(string target) => new(
        target,
        RootFolderRelocationMode.Relocate,
        true,
        "Moved Library",
        false,
        FileSystemCaseSensitivityMode.Auto);

    private async Task<(Guid RelocationId, int RootId)> SeedRetryableRelocationAsync()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-target-{Guid.NewGuid():N}");
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = "Library", Path = source };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();
        var relocation = new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = source,
            TargetPath = target,
            Mode = RootFolderRelocationMode.MetadataOnly,
            Status = RootFolderRelocationStatus.NeedsAttention,
            DesiredName = root.Name,
            Error = "Retry required."
        };
        db.RootFolderRelocations.Add(relocation);
        await db.SaveChangesAsync();
        return (relocation.Id, root.Id);
    }

    private sealed class FirstEntryPausingCoordinator : IFilesystemMutationCoordinator
    {
        private readonly FilesystemMutationCoordinator _inner = new();
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        public Task FirstEntered => _firstEntered.Task;

        public int EntryCount => Volatile.Read(ref _entries);

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                token => PauseFirstThenExecuteAsync(operation, token),
                cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                token => PauseFirstThenExecuteAsync(operation, token),
                cancellationToken);

        private async Task PauseFirstThenExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entries) == 1)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            await operation(cancellationToken);
        }

        private async Task<T> PauseFirstThenExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entries) == 1)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return await operation(cancellationToken);
        }
    }

    private sealed class TrackingCoordinator : IFilesystemMutationCoordinator
    {
        private readonly FilesystemMutationCoordinator _inner = new();
        private int _executing;

        public bool IsExecuting => Volatile.Read(ref _executing) != 0;

        public Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                async token =>
                {
                    Interlocked.Increment(ref _executing);
                    try
                    {
                        await operation(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _executing);
                    }
                },
                cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                async token =>
                {
                    Interlocked.Increment(ref _executing);
                    try
                    {
                        return await operation(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _executing);
                    }
                },
                cancellationToken);
    }

    private sealed class RecordingHubBroadcaster(Func<bool> isCoordinatorExecuting) : IHubBroadcaster
    {
        public int BroadcastCount { get; private set; }
        public bool CoordinatorWasExecuting { get; private set; }
        public object? Payload { get; private set; }

        public Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot) => Task.CompletedTask;

        public Task BroadcastAsync(
            string method,
            object payload,
            CancellationToken cancellationToken = default)
        {
            BroadcastCount++;
            CoordinatorWasExecuting |= isCoordinatorExecuting();
            Payload = payload;
            return Task.CompletedTask;
        }

        public Task BroadcastAsync(
            RealtimeHubTarget target,
            string method,
            object payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private RootFolderRelocationService CreateService() => new(
        _factory,
        new FileSystemSemanticsResolver(),
        new NoopHubBroadcaster(),
        TimeProvider.System,
        new FilesystemMutationCoordinator(),
        _operationCoordinator);

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TargetUnavailableSemanticsResolver(string unavailablePath) : IFileSystemSemanticsResolver
    {
        private readonly string _unavailablePath = Path.GetFullPath(unavailablePath);
        private readonly FileSystemSemanticsResolver _inner = new();

        public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
            string path,
            FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
            CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(
                fullPath,
                _unavailablePath,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    fullPath,
                    "Target filesystem identity became unavailable during finalization."));
            }

            return _inner.ResolveAsync(path, mode, cancellationToken);
        }
    }

    private sealed class ThrowingHubBroadcaster : IHubBroadcaster
    {
        public Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot) => Task.CompletedTask;

        public Task BroadcastAsync(
            string method,
            object payload,
            CancellationToken cancellationToken = default) =>
            throw new IOException("SignalR unavailable");

        public Task BroadcastAsync(
            RealtimeHubTarget target,
            string method,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
