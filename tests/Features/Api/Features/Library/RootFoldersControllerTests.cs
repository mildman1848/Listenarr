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
using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Listenarr.Infrastructure.Persistence.Repositories;
using AppRootFoldersController = Listenarr.Api.Features.Library.RootFoldersController;
using RootFoldersController = Listenarr.Tests.Features.Api.Features.Library.RootFoldersControllerTestAdapter;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "RootFoldersControllerTests")]
    [Trait("Category", "Api")]
    public sealed class RootFoldersControllerTests : BaseTests
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
            var createdAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
            var updatedAt = createdAt.AddMinutes(5);
            var svc = new FakeService();
            svc.Store.AddRange(new[] {
                new RootFolder
                {
                    Id = 1,
                    Name = "Root1",
                    Path = FileUtils.GetAbsolutePath("root1"),
                    CreatedAt = createdAt,
                    UpdatedAt = null
                },
                new RootFolder
                {
                    Id = 2,
                    Name = "Root2",
                    Path = FileUtils.GetAbsolutePath("root2"),
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                }
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
            Assert.Equal(createdAt, list[0].CreatedAt);
            Assert.Null(list[0].UpdatedAt);
            Assert.Equal(createdAt, list[1].CreatedAt);
            Assert.Equal(updatedAt, list[1].UpdatedAt);
        }

        [Fact]
        public void OpenApi_PreservesLegacyPutRouteAndTimestampContract()
        {
            using var factory = new Listenarr.Tests.Mocks.ListenarrWebApplicationFactory();
            var swaggerDocument = factory.Services
                .GetRequiredService<ISwaggerProvider>()
                .GetSwagger("v1");
            using var textWriter = new StringWriter();
            var writer = new OpenApiJsonWriter(textWriter);
            swaggerDocument.SerializeAs(OpenApiSpecVersion.OpenApi3_0, writer);
            using var document = JsonDocument.Parse(textWriter.ToString());
            var root = document.RootElement;
            var rootFolderPath = root
                .GetProperty("paths")
                .GetProperty("/api/v1/rootfolders/{id}");
            var put = rootFolderPath.GetProperty("put");
            var requestSchemaReference = put
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            const string schemaReferencePrefix = "#/components/schemas/";
            Assert.NotNull(requestSchemaReference);
            Assert.StartsWith(schemaReferencePrefix, requestSchemaReference, StringComparison.Ordinal);
            var schemaName = requestSchemaReference[schemaReferencePrefix.Length..];
            Assert.EndsWith(
                $".{nameof(RootFolder)}",
                schemaName,
                StringComparison.Ordinal);

            var schema = root
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(schemaName);
            var properties = schema.GetProperty("properties");
            var createdAt = properties.GetProperty("createdAt");
            Assert.Equal("string", createdAt.GetProperty("type").GetString());
            Assert.Equal("date-time", createdAt.GetProperty("format").GetString());
            Assert.False(
                createdAt.TryGetProperty("nullable", out var createdAtNullable)
                && createdAtNullable.GetBoolean());

            var updatedAt = properties.GetProperty("updatedAt");
            Assert.Equal("string", updatedAt.GetProperty("type").GetString());
            Assert.Equal("date-time", updatedAt.GetProperty("format").GetString());
            Assert.True(updatedAt.GetProperty("nullable").GetBoolean());

            var createRequestSchemaReference = root
                .GetProperty("paths")
                .GetProperty("/api/v1/rootfolders")
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            Assert.Equal(requestSchemaReference, createRequestSchemaReference);
        }

        [Fact]
        public void Update_IsExposedAsLegacyPutAction()
        {
            var method = typeof(AppRootFoldersController).GetMethod(nameof(AppRootFoldersController.Update));

            Assert.NotNull(method);
            var put = Assert.Single(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPutAttribute), inherit: true));
            Assert.Equal("{id}", ((Microsoft.AspNetCore.Mvc.HttpPutAttribute)put).Template);
            Assert.Empty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.NonActionAttribute), inherit: true));
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
        public async Task Create_LegacyRootFolderBody_IgnoresClientManagedIdentityAndTimestamps()
        {
            var svc = new FakeService();
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem());
            var clientCreatedAt = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clientUpdatedAt = clientCreatedAt.AddDays(1);

            var result = await controller.Create(new RootFolder
            {
                Id = 999,
                Name = "Legacy Create",
                Path = FileUtils.GetAbsolutePath("legacy-create"),
                IsDefault = true,
                CreatedAt = clientCreatedAt,
                UpdatedAt = clientUpdatedAt
            });

            var created = Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>(result);
            var payload = Assert.IsType<RootFolderDto>(created.Value);
            Assert.Equal(1, payload.Id);
            Assert.NotEqual(clientCreatedAt, payload.CreatedAt);
            Assert.Null(payload.UpdatedAt);
            Assert.True(payload.IsDefault);
        }

        [Fact]
        public async Task Create_DuplicatePath_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R1", Path = FileUtils.GetAbsolutePath("dup") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db), new LocalFileSystem());

            var req = new RootFolder
            {
                Name = "New",
                Path = FileUtils.GetAbsolutePath("dup"),
                IsDefault = false,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            };
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
        public async Task Update_DistinctPath_UsesDurableMetadataOnlyCompatibilityAdapter()
        {
            var sourcePath = FileUtils.GetAbsolutePath("legacy-source");
            var targetPath = FileUtils.GetAbsolutePath("legacy-target");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Old Name",
                Path = sourcePath,
                CreatedAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    var root = svc.Store.Single();
                    root.Path = command.TargetPath;
                    root.Name = command.DesiredName;
                    root.IsDefault = command.DesiredIsDefault;
                    root.CaseSensitivityMode = command.TargetCaseSensitivityMode;
                    root.UpdatedAt = DateTime.UtcNow;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);
            var request = new RootFolder
            {
                Id = 1,
                Name = "New Name",
                Path = targetPath,
                IsDefault = true,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };

            var result = await controller.Update(
                1,
                request,
                moveFiles: false,
                deleteEmptySource: false);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var payload = Assert.IsType<RootFolderDto>(ok.Value);
            Assert.Equal(targetPath, payload.Path);
            Assert.Equal("New Name", payload.Name);
            Assert.True(payload.IsDefault);
            Assert.NotNull(payload.UpdatedAt);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly
                    && !command.DeleteEmptySource
                    && command.DesiredName == "New Name"
                    && command.DesiredIsDefault
                    && command.TargetCaseSensitivityMode == FileSystemCaseSensitivityMode.Insensitive),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_InvalidPersistedSourceSyntax_StillAllowsMetadataRepairThroughDurableWorkflow()
        {
            var invalidSourcePath = "invalid::legacy-root";
            var targetPath = FileUtils.GetAbsolutePath("legacy-repaired-root");
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Legacy Root",
                Path = invalidSourcePath
            });
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                    invalidSourcePath,
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("invalid legacy source"));
            semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                    targetPath,
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(targetPath) ?? targetPath));
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    invalidSourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                semanticsResolver.Object,
                relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Repaired Root",
                    Path = targetPath
                },
                moveFiles: false);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.Mode == RootFolderRelocationMode.MetadataOnly
                    && command.TargetPath == targetPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_DistinctPathWithMoveFiles_ReturnsAcceptedAndUsesRelocateMode()
        {
            var sourcePath = FileUtils.GetAbsolutePath("legacy-physical-source");
            var targetPath = FileUtils.GetAbsolutePath("legacy-physical-target");
            var relocationId = Guid.NewGuid();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RootFolderPathChangeResult(
                    relocationId,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Pending,
                    1,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath
                },
                moveFiles: true,
                deleteEmptySource: true);

            var accepted = Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedAtRouteResult>(result);
            Assert.Equal("GetRootFolderRelocation", accepted.RouteName);
            Assert.Equal(relocationId, accepted.RouteValues!["id"]);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.Mode == RootFolderRelocationMode.Relocate
                    && command.DeleteEmptySource
                    && command.TargetPath == targetPath),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_CaseOnlyEditOnInsensitivePersistedRoot_PreservesCanonicalSpelling()
        {
            var sourcePath = FileUtils.GetAbsolutePath("LegacyCaseRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Renamed",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var payload = Assert.IsType<RootFolderDto>(ok.Value);
            Assert.Equal(sourcePath, payload.Path);
            Assert.Equal("Renamed", payload.Name);
            relocationService.Verify(service => service.StartAsync(
                It.IsAny<int>(),
                It.IsAny<RootFolderPathChangeCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Update_AutoRootUsesPersistedSensitiveResolutionInsteadOfFreshInsensitiveProbe()
        {
            var sourcePath = FileUtils.GetAbsolutePath("PersistedAutoSensitiveRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                BuildSemanticsResolver(FileSystemCaseSensitivity.Insensitive),
                relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Update_CaseOnlyEditOnSensitivePersistedRoot_UsesRelocation()
        {
            var sourcePath = FileUtils.GetAbsolutePath("LegacySensitiveRoot");
            var targetPath = sourcePath.ToLowerInvariant();
            var svc = new FakeService();
            svc.Store.Add(new RootFolder
            {
                Id = 1,
                Name = "Root",
                Path = sourcePath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            });
            var relocationService = new Mock<IRootFolderRelocationService>();
            relocationService.Setup(service => service.StartAsync(
                    1,
                    It.IsAny<RootFolderPathChangeCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, RootFolderPathChangeCommand, CancellationToken>((_, command, _) =>
                {
                    svc.Store.Single().Path = command.TargetPath;
                })
                .ReturnsAsync(new RootFolderPathChangeResult(
                    null,
                    1,
                    sourcePath,
                    targetPath,
                    RootFolderRelocationStatus.Completed,
                    0,
                    0,
                    null));
            var db = CreateDb();
            var controller = new RootFoldersController(
                svc,
                _fakeQueue,
                new EfAudiobookFileRepository(db),
                new AudiobookRepository(db),
                new LocalFileSystem(),
                relocationService: relocationService.Object);

            var result = await controller.Update(
                1,
                new RootFolder
                {
                    Id = 1,
                    Name = "Root",
                    Path = targetPath,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                });

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.Equal(targetPath, Assert.IsType<RootFolderDto>(ok.Value).Path);
            relocationService.Verify(service => service.StartAsync(
                1,
                It.Is<RootFolderPathChangeCommand>(command =>
                    command.TargetPath == targetPath
                    && command.Mode == RootFolderRelocationMode.MetadataOnly),
                It.IsAny<CancellationToken>()), Times.Once);
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
