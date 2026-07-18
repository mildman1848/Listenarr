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
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Persistence.Repositories;
using AppRootFoldersController = Listenarr.Api.Features.Library.RootFoldersController;
using RootFoldersController = Listenarr.Tests.Features.Api.Features.Library.RootFoldersControllerTestAdapter;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    public class RootFoldersControllerTests
    {
        private class FakeUnmatchedQueue : IUnmatchedScanQueueService
        {
            public UnmatchedScanJob? LastJob { get; set; }

            public System.Threading.Channels.ChannelReader<UnmatchedScanJob> Reader =>
                System.Threading.Channels.Channel.CreateUnbounded<UnmatchedScanJob>().Reader;
            public Task<Guid> EnqueueAsync(string rootFolderPath) => Task.FromResult(Guid.NewGuid());
            public bool TryGetJob(Guid id, out UnmatchedScanJob? job) { job = null; return false; }
            public void UpdateJob(Guid id, string status, List<UnmatchedFileResult>? results = null, string? error = null) { }
            public bool TryGetLastJobForPath(string rootFolderPath, out UnmatchedScanJob? job)
            {
                job = LastJob;
                return job != null && string.Equals(job.RootFolderPath, rootFolderPath, StringComparison.Ordinal);
            }
        }

        private static readonly IUnmatchedScanQueueService _fakeQueue = new FakeUnmatchedQueue();

        internal static IFileSystemSemanticsResolver BuildSemanticsResolver(
            FileSystemCaseSensitivity caseSensitivity = FileSystemCaseSensitivity.Sensitive)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, mode, _) =>
                {
                    var resolvedCaseSensitivity = mode == FileSystemCaseSensitivityMode.Insensitive
                        ? FileSystemCaseSensitivity.Insensitive
                        : mode == FileSystemCaseSensitivityMode.Sensitive
                            ? FileSystemCaseSensitivity.Sensitive
                            : caseSensitivity;
                    return ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            resolvedCaseSensitivity),
                        PathIdentityState.Valid,
                        Path.GetPathRoot(path) ?? path));
                });
            return resolver.Object;
        }

        private static ListenArrDbContext CreateDb() =>
            new ListenArrDbContext(
                new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

        private class FakeService : IRootFolderService
        {
            public List<RootFolder> Store { get; } = new List<RootFolder>();
            public bool ThrowPersistenceConflictOnDelete { get; set; }

            public Task<RootFolder?> GetDefaultAsync() => Task.FromResult(Store.Count > 0 ? Store.First() : null);

            public Task<List<RootFolder>> GetAllAsync() => Task.FromResult(new List<RootFolder>(Store));

            public Task<RootFolder?> GetByIdAsync(int id)
            {
                var f = Store.Find(s => s.Id == id);
                return Task.FromResult<RootFolder?>(f);
            }

            public Task<RootFolder> CreateAsync(RootFolder root)
            {
                // simulate duplicate path error
                if (Store.Exists(s => string.Equals(s.Path, root.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("A root with the same path already exists")
; root.Id = Store.Count + 1;
                Store.Add(root);
                return Task.FromResult(root);
            }

            public Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true)
            {
                var idx = Store.FindIndex(s => s.Id == root.Id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");

                // simulate invalid operation for certain paths
                if (root.Path?.Contains("/invalid/") == true) throw new InvalidOperationException("Invalid path")
;
                Store[idx] = root;
                return Task.FromResult(root);
            }

            public Task DeleteAsync(int id, int? reassignRootId = null)
            {
                var idx = Store.FindIndex(s => s.Id == id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");
                if (ThrowPersistenceConflictOnDelete)
                    throw new DbUpdateException("Delete failed due to relational constraint.", new Exception("FK"));

                // simulate in-use error if path contains "inuse"
                if (Store[idx].Path?.Contains("inuse") == true && reassignRootId == null)
                    throw new InvalidOperationException("Root folder in use")
;
                Store.RemoveAt(idx);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task GetAll_ReturnsAll()
        {
            var svc = new FakeService();
            svc.Store.AddRange(new[] {
                new RootFolder { Id = 1, Name = "Root1", Path = FileUtils.GetAbsolutePath("root1") },
                new RootFolder { Id = 2, Name = "Root2", Path = FileUtils.GetAbsolutePath("root2") }
            });

            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.GetAll();
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            var list = Assert.IsAssignableFrom<List<RootFolderDto>>(ok.Value);
            Assert.Equal(2, list.Count);
            Assert.All(
                list,
                item => Assert.Equal(
                    OperatingSystem.IsWindows() ? "Windows" : "Unix",
                    item.PathSyntax));
        }

        [Fact]
        public async Task Get_NotFound_Returns404()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Get(123);
            var notFound = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", notFound.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_DuplicatePath_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R1", Path = FileUtils.GetAbsolutePath("dup") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolderCreateRequest(
                "New",
                FileUtils.GetAbsolutePath("dup"),
                false,
                FileSystemCaseSensitivityMode.Auto);
            var res = await controller.Create(req);

            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("same path", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_IdMismatch_ReturnsBadRequest()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder { Id = 2, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(1, req);

            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("Id mismatch", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_NotFound_ReturnsNotFound()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder { Id = 99, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(99, req);

            var nf = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", nf.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetSavedUnmatched_FiltersUsingResolvedFolderSemantics()
        {
            var rootPath = Path.Join(Path.GetTempPath(), $"saved-unmatched-root-{Guid.NewGuid():N}");
            var resultPath = Path.Join(rootPath, "CaseBook.m4b");
            var trackedPath = Path.Join(rootPath, "casebook.m4b");
            Directory.CreateDirectory(rootPath);
            try
            {
                await File.WriteAllTextAsync(resultPath, "audio");
                var svc = new FakeService();
                svc.Store.Add(new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
                });
                var queue = new FakeUnmatchedQueue
                {
                    LastJob = new UnmatchedScanJob
                    {
                        RootFolderPath = rootPath,
                        Status = "Completed",
                        CompletedAt = DateTime.UtcNow,
                        Results =
                        [
                            new UnmatchedFileResult { FullPath = resultPath }
                        ]
                    }
                };
                var db = CreateDb();
                db.AudiobookFiles.Add(new AudiobookFile { Id = 1, Path = trackedPath, Format = "m4b" });
                await db.SaveChangesAsync();
                var resolver = BuildSemanticsResolver(FileSystemCaseSensitivity.Sensitive);
                var controller = new RootFoldersController(
                    svc,
                    queue,
                    new EfAudiobookFileRepository(db),
                    new AudiobookRepository(db),
                    new LocalFileSystem(),
                    resolver);

                var result = await controller.GetSavedUnmatched(1);

                var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
                var items = ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value);
                var list = Assert.IsAssignableFrom<List<UnmatchedFileResult>>(items);
                var item = Assert.Single(list);
                Assert.Equal(resultPath, item.FullPath);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public async Task GetSavedUnmatched_FiltersTrackedFilesAfterCanonicalizingPathSyntax()
        {
            var rootPath = Path.Join(Path.GetTempPath(), $"saved-unmatched-canonical-root-{Guid.NewGuid():N}");
            var resultPath = Path.Join(rootPath, "Book", "book.m4b");
            var trackedPath = Path.Join(rootPath, "Book", ".", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            try
            {
                await File.WriteAllTextAsync(resultPath, "audio");
                var svc = new FakeService();
                svc.Store.Add(new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = rootPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
                });
                var queue = new FakeUnmatchedQueue
                {
                    LastJob = new UnmatchedScanJob
                    {
                        RootFolderPath = rootPath,
                        Status = "Completed",
                        CompletedAt = DateTime.UtcNow,
                        Results =
                        [
                            new UnmatchedFileResult { FullPath = resultPath }
                        ]
                    }
                };
                var db = CreateDb();
                db.AudiobookFiles.Add(new AudiobookFile { Id = 1, Path = trackedPath, Format = "m4b" });
                await db.SaveChangesAsync();
                var controller = new RootFoldersController(
                    svc,
                    queue,
                    new EfAudiobookFileRepository(db),
                    new AudiobookRepository(db),
                    new LocalFileSystem());

                var result = await controller.GetSavedUnmatched(1);

                var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
                var items = ok.Value!.GetType().GetProperty("items")!.GetValue(ok.Value);
                var list = Assert.IsAssignableFrom<List<UnmatchedFileResult>>(items);
                Assert.Empty(list);
            }
            finally
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        [Fact]
        public async Task Delete_InUseWithoutReassign_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Delete(1, null);
            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("in use", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_WithReassign_Succeeds()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            svc.Store.Add(new RootFolder { Id = 2, Name = "R2", Path = FileUtils.GetAbsolutePath("r") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var res = await controller.Delete(1, 2);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            Assert.Contains("Deleted", ok.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_PersistenceConflict_ReturnsConflict()
        {
            var svc = new FakeService
            {
                ThrowPersistenceConflictOnDelete = true
            };
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("delete-conflict") });
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());

            var result = await controller.Delete(1, null);

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
            Assert.Contains("persisted references", conflict.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class RootFoldersControllerTestAdapter : AppRootFoldersController
    {
        public RootFoldersControllerTestAdapter(
            IRootFolderService service,
            IUnmatchedScanQueueService unmatchedQueue,
            IAudiobookFileRepository fileRepository,
            IAudiobookRepository audiobookRepository,
            IFileSystem fileSystem,
            IFileSystemSemanticsResolver? semanticsResolver = null,
            IRootFolderRelocationService? relocationService = null)
            : base(
                service,
                unmatchedQueue,
                fileRepository,
                audiobookRepository,
                fileSystem,
                semanticsResolver ?? RootFoldersControllerTests.BuildSemanticsResolver(),
                relocationService ?? Mock.Of<IRootFolderRelocationService>())
        {
        }

    }
}
