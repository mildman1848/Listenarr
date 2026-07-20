using Microsoft.EntityFrameworkCore;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "EfLibraryDirectoryOwnershipStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfLibraryDirectoryOwnershipStoreTests : BaseTests
{
    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"directory-ownership-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"directory-ownership-root-{Guid.NewGuid():N}");
    private IDbContextFactory<ListenArrDbContext> _factory = null!;
    private EfLibraryDirectoryOwnershipStore _store = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _store = new EfLibraryDirectoryOwnershipStore(_factory, TimeProvider.System);
    }

    public override async Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        await base.DisposeAsync();
    }

    [Fact]
    public async Task RecordCreatedAsync_PersistsIdentityAndMatchingPhysicalMarkers()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);

        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test",
                Guid.NewGuid(),
                AudiobookId: 7));

        Assert.NotEqual(0, ownership.Id);
        Assert.False(string.IsNullOrWhiteSpace(ownership.PathOwnershipKey));
        Assert.False(string.IsNullOrWhiteSpace(ownership.OwnershipToken));
        Assert.True(File.Exists(Path.Join(directory, LibraryDirectoryOwnershipMarker.FileName)));
        Assert.Single(Directory.EnumerateFiles(
            _root,
            ".listenarr-directory-owner-*.json",
            SearchOption.TopDirectoryOnly));
        LibraryDirectoryOwnershipMarker.Validate(ownership, directory);

        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        Assert.Equal(ownership.Id, resolution.Ownership?.Id);
    }

    [Fact]
    public async Task RecordCreatedAsync_IsIdempotentForTheSameIdentity()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        var claim = new LibraryDirectoryOwnershipClaim(
            directory,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");

        var first = await _store.RecordCreatedAsync(claim);
        var second = await _store.RecordCreatedAsync(claim);

        Assert.Equal(first.Id, second.Id);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.LibraryDirectoryOwnerships.ToListAsync());
    }

    [Fact]
    public async Task RecordCreatedAsync_CrossSensitivityAliasBecomesConflict()
    {
        var directory = Path.Join(_root, "Library");
        Directory.CreateDirectory(directory);
        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                new FileSystemPathSemantics(
                    syntax,
                    FileSystemCaseSensitivity.Sensitive),
                "test"));
        var alias = Path.Join(_root, "library");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    alias,
                    new FileSystemPathSemantics(
                        syntax,
                        FileSystemCaseSensitivity.Insensitive),
                    "test")));

        Assert.Contains("conflicts", exception.Message, StringComparison.OrdinalIgnoreCase);
        var resolution = await _store.ResolveOwnedAsync(
            directory,
            new FileSystemPathSemantics(
                syntax,
                FileSystemCaseSensitivity.Sensitive));
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Conflict, resolution.State);
    }

    [Fact]
    public async Task PhysicalPathReplacementWithoutInsideMarkerFailsValidation()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));

        File.Delete(Path.Join(directory, LibraryDirectoryOwnershipMarker.FileName));
        Directory.Delete(directory, recursive: false);
        Directory.CreateDirectory(directory);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipMarker.Validate(ownership, directory));
        Assert.Contains("marker is missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_ClaimsOnlyDirectoriesCreatedExclusively()
    {
        var destination = Path.Join(_root, "Author", "Book");

        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test",
            Guid.NewGuid(),
            audiobookId: 7);

        Assert.Equal(2, ownerships.Count);
        Assert.True(Directory.Exists(destination));
        var rootResolution = await _store.ResolveOwnedAsync(
            _root,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, rootResolution.State);
        foreach (var ownership in ownerships)
        {
            LibraryDirectoryOwnershipMarker.Validate(
                ownership,
                ownership.CanonicalPath);
        }
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_PersistenceFailureRemovesOnlyUnchangedEmptyCreation()
    {
        var destination = Path.Join(_root, "FailedEmptyCreation");
        var store = new EfLibraryDirectoryOwnershipStore(
            new FailFirstContextCreationFactory(_factory),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureCreatedHierarchyAsync(
                destination,
                _root,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-failed-create"));

        Assert.False(Directory.Exists(destination));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_PersistenceFailurePreservesChangedCreation()
    {
        var destination = Path.Join(_root, "FailedChangedCreation");
        var foreignFile = Path.Join(destination, "foreign.txt");
        var store = new EfLibraryDirectoryOwnershipStore(
            new FailFirstContextCreationFactory(
                _factory,
                () => File.WriteAllText(foreignFile, "foreign")),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureCreatedHierarchyAsync(
                destination,
                _root,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-failed-changed-create"));

        Assert.True(Directory.Exists(destination));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignFile));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_CancellationAfterExclusiveCreationFinishesDurableClaim()
    {
        var destination = Path.Join(_root, "CanceledAfterCreate");
        using var cancellation = new CancellationTokenSource();
        var store = new EfLibraryDirectoryOwnershipStore(
            new CancelOnFirstContextCreationFactory(_factory, cancellation),
            TimeProvider.System);

        var ownerships = await store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test-canceled-after-create",
            cancellationToken: cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        var ownership = Assert.Single(ownerships);
        LibraryDirectoryOwnershipMarker.Validate(ownership, destination);
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        Assert.Equal(ownership.Id, resolution.Ownership?.Id);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_RepairsMarkersOnlyForExistingDurableClaim()
    {
        var destination = Path.Join(_root, "Author", "Book");
        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");
        var ownership = ownerships.Single(item =>
            FileSystemPathIdentity.AreEquivalent(
                item.CanonicalPath,
                destination,
                FileSystemPathSemantics.CurrentHostDefault));
        foreach (var markerPath in LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(markerPath, FileAttributes.Normal);
            }
            File.Delete(markerPath);
        }

        var repaired = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test-retry");

        Assert.Empty(repaired);
        LibraryDirectoryOwnershipMarker.Validate(ownership, destination);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_DoesNotClaimPreExistingParent()
    {
        var author = Path.Join(_root, "Author");
        var destination = Path.Join(author, "Book");
        Directory.CreateDirectory(author);

        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");

        var ownership = Assert.Single(ownerships);
        Assert.Equal(
            FileSystemPathIdentity.Canonicalize(
                destination,
                FileSystemPathSemantics.CurrentHostDefault.Syntax),
            ownership.CanonicalPath);
        var parentResolution = await _store.ResolveOwnedAsync(
            author,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, parentResolution.State);
    }

    [Fact]
    public async Task ExclusiveDirectoryCreator_ConcurrentAttemptsHaveSingleCreator()
    {
        var directory = Path.Join(_root, "Concurrent");
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ExclusiveDirectoryCreator.TryCreate(directory))));

        Assert.Single(results, created => created);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task RemovingDirectory_CanCompleteAfterDirectoryDeletionAndRestart()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);

        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory, recursive: false);

        var restartedStore = new EfLibraryDirectoryOwnershipStore(_factory, TimeProvider.System);
        var resolution = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        var removing = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
        Assert.Equal(LibraryDirectoryOwnershipState.Removing, removing.State);
        await restartedStore.MarkRemovedAsync(removing.Id, ownershipKey);
        Assert.True(LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
            removing,
            out var markerDeleteReason), markerDeleteReason);

        var removed = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, removed.State);
    }

    [Fact]
    public async Task RecordCreatedAsync_RemovesRetiredSiblingMarkerFromPriorOwnership()
    {
        var directory = Path.Join(_root, "Recreated");
        Directory.CreateDirectory(directory);
        var prior = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(prior.PathOwnershipKey);
        var retiredSiblingMarker = LibraryDirectoryOwnershipMarker.GetMarkerPaths(prior)
            .Single(path => !FileSystemPathIdentity.IsSameOrInside(
                path,
                directory,
                FileSystemPathSemantics.CurrentHostDefault));

        await _store.BeginRemovalAsync(prior.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(prior, directory);
        Directory.Delete(directory, recursive: false);
        await _store.MarkRemovedAsync(prior.Id, ownershipKey);
        Assert.True(File.Exists(retiredSiblingMarker));
        Directory.CreateDirectory(directory);

        var recreated = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-recreated"));

        Assert.NotEqual(prior.Id, recreated.Id);
        Assert.NotEqual(prior.OwnershipToken, recreated.OwnershipToken);
        Assert.False(File.Exists(retiredSiblingMarker));
        LibraryDirectoryOwnershipMarker.Validate(recreated, directory);
    }

    [Fact]
    public async Task RemovalPath_InvalidOwnershipTokenCannotEscapeParent()
    {
        var directory = Path.Join(_root, "InvalidToken");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var insideMarker = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        ownership.OwnershipToken = $"..{Path.DirectorySeparatorChar}outside";

        var quarantineException = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership));
        var markerException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipMarker.EnsureAsync(
                ownership,
                CancellationToken.None));

        Assert.Contains("token is invalid", quarantineException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token is invalid", markerException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(insideMarker));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task RemovalPath_FileReplacementAtOriginalPathFailsClosed()
    {
        var directory = Path.Join(_root, "OriginalFileReplacement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        ownership.State = LibraryDirectoryOwnershipState.Removing;
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory, recursive: false);
        await File.WriteAllTextAsync(directory, "user file");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(ownership));

        Assert.Contains("occupied by a file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user file", await File.ReadAllTextAsync(directory));
    }

    [Fact]
    public async Task RemovalPath_FileAtQuarantinePathFailsClosed()
    {
        var directory = Path.Join(_root, "QuarantineFileReplacement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        ownership.State = LibraryDirectoryOwnershipState.Removing;
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory, recursive: false);
        var quarantinePath = LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership);
        await File.WriteAllTextAsync(quarantinePath, "foreign file");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(ownership));

        Assert.Contains("occupied by a file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("foreign file", await File.ReadAllTextAsync(quarantinePath));
    }

    [Fact]
    public async Task RecordCreatedAsync_CorruptRemovedIdentityDoesNotBlockNewClaim()
    {
        var directory = Path.Join(_root, "RecreatedAfterCorruptRetiredRow");
        Directory.CreateDirectory(directory);
        var prior = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(prior.PathOwnershipKey);

        await _store.BeginRemovalAsync(prior.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(prior, directory);
        Directory.Delete(directory, recursive: false);
        await _store.MarkRemovedAsync(prior.Id, ownershipKey);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var retired = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == prior.Id);
            retired.PathIdentityBoundary = "relative-invalid-boundary";
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(directory);

        var recreated = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-recreated"));

        Assert.NotEqual(prior.Id, recreated.Id);
        LibraryDirectoryOwnershipMarker.Validate(recreated, directory);
    }

    private sealed class FailFirstContextCreationFactory(
        IDbContextFactory<ListenArrDbContext> inner,
        Action? beforeFailure = null)
        : IDbContextFactory<ListenArrDbContext>
    {
        private int _failed;

        public ListenArrDbContext CreateDbContext()
        {
            FailOnce();
            return inner.CreateDbContext();
        }

        public async Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void FailOnce()
        {
            if (Interlocked.Exchange(ref _failed, 1) != 0)
            {
                return;
            }

            beforeFailure?.Invoke();
            throw new InvalidOperationException("Injected ownership persistence failure.");
        }
    }

    private sealed class CancelOnFirstContextCreationFactory(
        IDbContextFactory<ListenArrDbContext> inner,
        CancellationTokenSource cancellation)
        : IDbContextFactory<ListenArrDbContext>
    {
        private int _canceled;

        public ListenArrDbContext CreateDbContext()
        {
            CancelRequest();
            return inner.CreateDbContext();
        }

        public async Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            CancelRequest();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void CancelRequest()
        {
            if (Interlocked.Exchange(ref _canceled, 1) == 0)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
