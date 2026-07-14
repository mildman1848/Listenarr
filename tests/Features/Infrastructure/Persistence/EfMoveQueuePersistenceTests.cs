/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class EfMoveQueuePersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), "listenarr-tests", $"move-dedupe-{Guid.NewGuid():N}.db");
    private IDbContextFactory<ListenArrDbContext> _factory = null!;

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
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ActiveDeduplicationKey_IsUniqueUntilTerminalStatus()
    {
        var persistence = CreatePersistence();
        var first = CreateJob("42:/LIBRARY/BOOK");
        var duplicate = CreateJob("42:/LIBRARY/BOOK");

        await persistence.AddAsync(first);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => persistence.AddAsync(duplicate));

        var claimedGeneration = await persistence.TryClaimAsync(
            first.Id,
            "worker-a",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2));
        await persistence.UpdateStatusAsync(
            first.Id,
            "worker-a",
            claimedGeneration.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            DateTimeOffset.UtcNow);
        await persistence.AddAsync(duplicate);

        Assert.Equal(duplicate.Id, (await persistence.GetActiveByKeyAsync("42:/LIBRARY/BOOK"))?.Id);
    }

    [Fact]
    public async Task SourceCleanupBoundary_RoundTripsWithMoveJob()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:cleanup-boundary");
        job.SourcePath = "/downloads/Author/Title/test";
        job.SourceCleanupBoundary = "/downloads";

        await persistence.AddAsync(job);
        var persisted = await persistence.GetByIdAsync(job.Id);

        Assert.NotNull(persisted);
        Assert.Equal("/downloads", persisted!.SourceCleanupBoundary);
    }

    [Fact]
    public async Task ReconcileIdentityKeys_SelectsMostAdvancedLegacyDuplicate()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = "/downloads/book",
                    RequestedPath = "/library/book",
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.Planned,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:first"
                },
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = "/downloads/book",
                    RequestedPath = "/library/book",
                    Status = MoveJobStatus.Running,
                    Phase = MoveJobPhase.Published,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:second"
                });
            await db.SaveChangesAsync();
        }

        var persistence = CreatePersistence();
        await persistence.ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.OrderBy(job => job.Phase).ToListAsync();
        Assert.Equal(MoveJobStatus.Superseded, jobs[0].Status);
        Assert.Null(jobs[0].ActiveDeduplicationKey);
        Assert.Equal(MoveJobStatus.Running, jobs[1].Status);
        Assert.StartsWith("v3:move:42:", jobs[1].ActiveDeduplicationKey);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_MultipleEvidenceBearingDuplicates_RequireAttention()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-conflict-{Guid.NewGuid():N}");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var first = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source-a",
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:first",
                LeaseOwner = "worker-a",
                LeaseGeneration = 1,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            var second = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source-b",
                Status = MoveJobStatus.RetryScheduled,
                Phase = MoveJobPhase.Copying,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:second"
            };
            db.MoveJobs.AddRange(first, second);
            db.MoveJobEntries.AddRange(
                new MoveJobEntry
                {
                    MoveJobId = first.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    Sha256 = new string('a', 64)
                },
                new MoveJobEntry
                {
                    MoveJobId = second.Id,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    Sha256 = new string('b', 64)
                });
            await db.SaveChangesAsync();
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Equal(MoveFailureKind.Verification, job.FailureKind);
            Assert.Null(job.ActiveDeduplicationKey);
            Assert.Null(job.LeaseOwner);
        });
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_AuthoritativeMarkerPreservesOwnerAndSupersedesCleanDuplicate()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-marker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        MoveJob owner;
        MoveJob duplicate;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            owner = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source",
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:owner"
            };
            duplicate = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source",
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:duplicate"
            };
            db.MoveJobs.AddRange(owner, duplicate);
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(
            Path.Join(target, $".listenarr-move-{owner.Id:N}.pending"),
            "owned evidence");

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persistedOwner = await verification.MoveJobs.AsNoTracking()
            .SingleAsync(job => job.Id == owner.Id);
        var persistedDuplicate = await verification.MoveJobs.AsNoTracking()
            .SingleAsync(job => job.Id == duplicate.Id);
        Assert.Equal(MoveJobStatus.Queued, persistedOwner.Status);
        Assert.NotNull(persistedOwner.ActiveDeduplicationKey);
        Assert.Equal(MoveJobStatus.Superseded, persistedDuplicate.Status);
        Assert.Null(persistedDuplicate.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_MismatchedTargetOwnershipMarkerRequiresAttention()
    {
        var target = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-marker-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        MoveJob owner;
        MoveJob duplicate;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            owner = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = target + "-source",
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:owner"
            };
            duplicate = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = owner.SourcePath,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:duplicate"
            };
            db.MoveJobs.AddRange(owner, duplicate);
            await db.SaveChangesAsync();
        }

        var targetParent = Path.GetDirectoryName(target)!;
        await File.WriteAllTextAsync(
            Path.Join(target, ".listenarr-temp-owner.json"),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                ArtifactType = "temporary-directory",
                JobId = owner.Id,
                Source = owner.SourcePath + "-wrong",
                Target = target,
                DirectoryPath = Path.Join(
                    targetParent,
                    Path.GetFileName(target) + ".tmp-" + owner.Id.ToString("N"))
            }));

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs.AsNoTracking().ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(MoveJobStatus.NeedsAttention, job.Status);
            Assert.Contains("ownership marker", job.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(job.ActiveDeduplicationKey);
        });
    }

    [Theory]
    [InlineData("source-marker-write")]
    [InlineData("target-partial")]
    [InlineData("target-ownership-write")]
    [InlineData("target-cleanup")]
    public async Task ReconcileIdentityKeysAsync_ResidualFilesystemEvidencePreservesOwner(
        string evidenceKind)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"move-reconcile-residual-{Guid.NewGuid():N}");
        var source = Path.Join(root, "source");
        var target = Path.Join(root, "target");
        MoveJob owner;
        MoveJob duplicate;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            owner = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = source,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:owner"
            };
            duplicate = new MoveJob
            {
                AudiobookId = 42,
                RequestedPath = target,
                SourcePath = source,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.Planned,
                IdentityKeyVersion = 1,
                ActiveDeduplicationKey = "legacy:duplicate"
            };
            db.MoveJobs.AddRange(owner, duplicate);
            await db.SaveChangesAsync();
        }

        Directory.CreateDirectory(root);
        switch (evidenceKind)
        {
            case "source-marker-write":
                Directory.CreateDirectory(source);
                await File.WriteAllTextAsync(
                    Path.Join(
                        source,
                        $".listenarr-move-{owner.Id:N}.pending.writing-{owner.Id:N}-g1-{Guid.NewGuid():N}"),
                    "incomplete marker");
                break;
            case "target-partial":
                Directory.CreateDirectory(Path.Join(target, "nested"));
                await File.WriteAllTextAsync(
                    Path.Join(target, "nested", $"book.m4b.listenarr-{owner.Id:N}.partial"),
                    "partial");
                break;
            case "target-ownership-write":
                Directory.CreateDirectory(target);
                await File.WriteAllTextAsync(
                    Path.Join(
                        target,
                        $".listenarr-temp-owner.json.writing-{owner.Id:N}-g1-{Guid.NewGuid():N}"),
                    "incomplete ownership marker");
                break;
            case "target-cleanup":
                await File.WriteAllTextAsync(
                    Path.Join(root, $".listenarr-temp-directory-{owner.Id:N}.cleanup.json"),
                    "cleanup evidence");
                break;
            default:
                throw new InvalidOperationException($"Unknown evidence kind: {evidenceKind}");
        }

        await CreatePersistence().ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persistedOwner = await verification.MoveJobs.AsNoTracking()
            .SingleAsync(job => job.Id == owner.Id);
        var persistedDuplicate = await verification.MoveJobs.AsNoTracking()
            .SingleAsync(job => job.Id == duplicate.Id);
        Assert.Equal(MoveJobStatus.Queued, persistedOwner.Status);
        Assert.NotNull(persistedOwner.ActiveDeduplicationKey);
        Assert.Equal(MoveJobStatus.Superseded, persistedDuplicate.Status);
        Assert.Null(persistedDuplicate.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task ReconcileIdentityKeysAsync_MalformedLegacyJobMarksNeedsAttentionAndContinues()
    {
        var throwingPath = Path.GetFullPath("/library/bad-book");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MoveJobs.AddRange(
                new MoveJob
                {
                    AudiobookId = 42,
                    SourcePath = Path.GetFullPath("/downloads/bad-book"),
                    RequestedPath = throwingPath,
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.None,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:bad"
                },
                new MoveJob
                {
                    AudiobookId = 43,
                    SourcePath = "/downloads/good-book",
                    RequestedPath = "/library/good-book",
                    Status = MoveJobStatus.Queued,
                    Phase = MoveJobPhase.None,
                    IdentityKeyVersion = 1,
                    ActiveDeduplicationKey = "legacy:good"
                });
            await db.SaveChangesAsync();
        }

        var resolver = BuildSemanticsResolver(path =>
            string.Equals(path, throwingPath, StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated invalid path")
                : null);
        var persistence = CreatePersistence(resolver);

        await persistence.ReconcileIdentityKeysAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var bad = await verification.MoveJobs.SingleAsync(job => job.AudiobookId == 42);
        var good = await verification.MoveJobs.SingleAsync(job => job.AudiobookId == 43);
        Assert.Equal(MoveJobStatus.NeedsAttention, bad.Status);
        Assert.Equal(MoveFailureKind.Verification, bad.FailureKind);
        Assert.Contains("Move path identity could not be reconciled", bad.Error, StringComparison.Ordinal);
        Assert.Null(bad.ActiveDeduplicationKey);
        Assert.Equal(MoveJobStatus.Queued, good.Status);
        Assert.StartsWith("v3:move:43:", good.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentWorkers_OnlyOneAcquiresLease()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:claim");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        var claims = await Task.WhenAll(
            persistence.TryClaimAsync(job.Id, "worker-a", now, now.AddMinutes(2)),
            persistence.TryClaimAsync(job.Id, "worker-b", now, now.AddMinutes(2)));

        Assert.Single(claims, generation => generation.HasValue);
        var claimedJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, claimedJob!.Status);
        Assert.Contains(claimedJob.LeaseOwner, new[] { "worker-a", "worker-b" });
        Assert.Equal(1, claimedJob.LeaseGeneration);
    }

    [Fact]
    public async Task TryClaimAsync_ExpiredLease_IncrementsLeaseGeneration()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:reclaim");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2)));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.LeaseExpiresAt = now.AddSeconds(-1).UtcDateTime;
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now,
            now.AddMinutes(2)));

        var reclaimedJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(2, reclaimedJob!.LeaseGeneration);
        Assert.Equal("worker-b", reclaimedJob.LeaseOwner);
    }

    [Fact]
    public async Task MatchingUnexpiredOwnership_CanHeartbeatAndUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:valid");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, generation);

        Assert.Equal(
            MoveHeartbeatOutcome.Renewed,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                generation.GetValueOrDefault(),
                now.AddSeconds(1),
                now.AddMinutes(3)));
        var beforeIncrement = await persistence.GetByIdAsync(job.Id);
        Assert.True(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            now.AddSeconds(1)));
        var afterIncrement = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(1, afterIncrement!.AttemptCount);
        Assert.Equal(beforeIncrement!.Status, afterIncrement.Status);
        Assert.Equal(beforeIncrement.Phase, afterIncrement.Phase);
        Assert.Equal(beforeIncrement.ActiveDeduplicationKey, afterIncrement.ActiveDeduplicationKey);
        Assert.Equal(beforeIncrement.LeaseOwner, afterIncrement.LeaseOwner);
        Assert.Equal(beforeIncrement.LeaseGeneration, afterIncrement.LeaseGeneration);
        Assert.Equal(beforeIncrement.LeaseExpiresAt, afterIncrement.LeaseExpiresAt);
        Assert.True(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now.AddSeconds(2)));

        var completed = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Completed, completed!.Status);
        Assert.Null(completed.ActiveDeduplicationKey);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseExpiresAt);
    }

    [Fact]
    public async Task ExpiredOwnership_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:expired");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddSeconds(1));
        Assert.Equal(1, generation);

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                generation.GetValueOrDefault(),
                now.AddSeconds(2),
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            now.AddSeconds(2)));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now.AddSeconds(2)));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
        Assert.Equal(2, await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now.AddSeconds(2),
            now.AddMinutes(4)));
    }

    [Fact]
    public async Task WrongOwner_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:owner");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-b",
                generation.GetValueOrDefault(),
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-b",
            generation.GetValueOrDefault(),
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-b",
            generation.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
    }

    [Fact]
    public async Task NonRunningJob_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:queued");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                1,
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            1,
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            1,
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));
        Assert.Equal(0, (await persistence.GetByIdAsync(job.Id))!.AttemptCount);
    }

    [Fact]
    public async Task StaleLeaseGeneration_CannotHeartbeatOrUpdateStatus()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:fenced");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var staleGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, staleGeneration);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.LeaseExpiresAt = now.AddSeconds(-1).UtcDateTime;
            await db.SaveChangesAsync();
        }

        var currentGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-b",
            now,
            now.AddMinutes(2));
        Assert.Equal(2, currentGeneration);

        Assert.Equal(
            MoveHeartbeatOutcome.Lost,
            await persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                staleGeneration.GetValueOrDefault(),
                now,
                now.AddMinutes(3)));
        Assert.False(await persistence.TryIncrementAttemptAsync(
            job.Id,
            "worker-a",
            staleGeneration.GetValueOrDefault(),
            now));
        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            staleGeneration.GetValueOrDefault(),
            MoveJobStatus.Completed,
            MoveJobPhase.Finalizing,
            null,
            MoveFailureKind.None,
            now));

        var currentJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, currentJob!.Status);
        Assert.Equal("worker-b", currentJob.LeaseOwner);
        Assert.Equal(2, currentJob.LeaseGeneration);
        Assert.Equal(0, currentJob.AttemptCount);
    }

    [Fact]
    public async Task TerminalReconciliationState_WithSameGeneration_CannotBeOverwrittenByStaleWorker()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v2:move:42:s:superseded");
        await persistence.AddAsync(job);
        var now = DateTimeOffset.UtcNow;
        var generation = await persistence.TryClaimAsync(
            job.Id,
            "worker-a",
            now,
            now.AddMinutes(2));
        Assert.Equal(1, generation);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var claimedJob = await db.MoveJobs.SingleAsync(candidate => candidate.Id == job.Id);
            claimedJob.Status = MoveJobStatus.Superseded;
            claimedJob.Error = "Superseded by reconciliation.";
            claimedJob.LeaseOwner = null;
            claimedJob.LeaseExpiresAt = null;
            await db.SaveChangesAsync();
        }

        Assert.False(await persistence.UpdateStatusAsync(
            job.Id,
            "worker-a",
            generation.GetValueOrDefault(),
            MoveJobStatus.Failed,
            MoveJobPhase.Finalizing,
            "stale failure",
            MoveFailureKind.Unknown,
            now));

        var currentJob = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Superseded, currentJob!.Status);
        Assert.Equal("Superseded by reconciliation.", currentJob.Error);
    }

    [Theory]
    [InlineData("reconcile")]
    [InlineData("health")]
    [InlineData("requeue")]
    [InlineData("claim")]
    [InlineData("heartbeat")]
    public async Task ProviderFailure_IsTranslatedToPersistenceException(string operation)
    {
        var unavailablePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"missing-{Guid.NewGuid():N}",
            "move.db");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={unavailablePath};Mode=ReadWrite;Pooling=False")
            .Options;
        var persistence = new EfMoveQueuePersistence(
            new TestDbContextFactory(options),
            BuildSemanticsResolver());
        var job = CreateJob("v2:move:42:s:unavailable");
        var now = DateTimeOffset.UtcNow;

        Task operationTask = operation switch
        {
            "reconcile" => persistence.ReconcileIdentityKeysAsync(),
            "health" => persistence.GetHealthAsync(now),
            "requeue" => persistence.RequeueAsync(CreateRequeueCommand(
                job,
                "v3:move:42:s:unavailable-requeue")),
            "claim" => persistence.TryClaimAsync(job.Id, "worker-a", now, now.AddMinutes(2)),
            "heartbeat" => persistence.HeartbeatAsync(
                job.Id,
                "worker-a",
                1,
                now,
                now.AddMinutes(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        await Assert.ThrowsAsync<PersistenceException>(() => operationTask);
    }

    [Fact]
    public async Task RequeueAsync_PersistsResetRetryAndLeaseState()
    {
        var persistence = CreatePersistence();
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var job = CreateJob("v2:move:42:s:requeue-reset");
        job.Status = MoveJobStatus.Failed;
        job.Phase = MoveJobPhase.CleaningSource;
        job.Error = "verification failed";
        job.FailureKind = MoveFailureKind.Verification;
        job.NextAttemptAt = future.UtcDateTime;
        job.LeaseOwner = "worker-a";
        job.LeaseExpiresAt = future.UtcDateTime;
        job.LeaseGeneration = 3;
        job.AttemptCount = 2;
        await persistence.AddAsync(job);

        var result = await persistence.RequeueAsync(CreateRequeueCommand(
            job,
            "v3:move:42:s:requeue-reset-new"));

        Assert.Equal(MoveRequeueOutcome.Requeued, result.Outcome);
        var persisted = await persistence.GetByIdAsync(job.Id);
        Assert.NotNull(persisted);
        Assert.Equal(MoveJobStatus.Queued, persisted.Status);
        Assert.Equal(MoveJobPhase.None, persisted.Phase);
        Assert.Null(persisted.Error);
        Assert.Equal(MoveFailureKind.None, persisted.FailureKind);
        Assert.Null(persisted.NextAttemptAt);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.LeaseExpiresAt);
        Assert.Equal("v3:move:42:s:requeue-reset-new", persisted.ActiveDeduplicationKey);
        Assert.Equal(3, persisted.LeaseGeneration);
        Assert.Equal(0, persisted.AttemptCount);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book"), persisted.SourcePath);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book"), persisted.RequestedPath);
        Assert.Equal(FileSystemPathSemantics.CurrentHostDefault.Syntax, persisted.SourcePathSyntax);
        Assert.Equal(FileSystemPathSemantics.CurrentHostDefault.Syntax, persisted.TargetPathSyntax);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.SourceCaseSensitivity);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.TargetCaseSensitivity);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, persisted.SourceCaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, persisted.TargetCaseSensitivityMode);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book"), persisted.SourceIdentityBoundary);
        Assert.Equal(FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book"), persisted.TargetIdentityBoundary);
        Assert.Equal(3, persisted.IdentityKeyVersion);
    }

    [Fact]
    public async Task RequeueAsync_ProcessRestartRecoversCommittedRepairBeforePublication()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v3:move:42:s:restart-requeue");
        job.Status = MoveJobStatus.NeedsAttention;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(
            job,
            "v3:move:42:s:restart-requeue-new");

        Assert.Equal(
            MoveRequeueOutcome.Requeued,
            (await persistence.RequeueAsync(command)).Outcome);
        var relocationService = new Mock<IRootFolderRelocationService>();
        relocationService.Setup(service => service.ReconcileActiveAsync(
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var restartedQueue = new MoveQueueService(
            Mock.Of<ILogger<MoveQueueService>>(),
            persistence,
            Mock.Of<IHubBroadcaster>(),
            TimeProvider.System,
            BuildSemanticsResolver(),
            relocationService.Object,
            Mock.Of<IFilesystemMutationCoordinator>());

        await restartedQueue.RecoverActiveJobsAsync();

        Assert.True(restartedQueue.Reader.TryRead(out var recovered));
        Assert.Equal(job.Id, recovered.Id);
        Assert.True(recovered.TryGetSourceIdentity(out var recoveredSourceIdentity));
        Assert.True(recovered.TryGetTargetIdentity(out var recoveredTargetIdentity));
        Assert.True(FileSystemPathIdentity.AreEquivalent(
            command.SourcePath,
            recovered.SourcePath,
            recoveredSourceIdentity.Semantics));
        Assert.True(FileSystemPathIdentity.AreEquivalent(
            command.TargetPath,
            recovered.RequestedPath,
            recoveredTargetIdentity.Semantics));
    }

    [Fact]
    public async Task RequeueAsync_ConcurrentClaim_DoesNotOverwriteNewerLeaseState()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v3:move:42:s:stale-requeue");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(
            job,
            "v3:move:42:s:stale-requeue-new");
        Assert.Equal(
            MoveRequeueOutcome.Requeued,
            (await persistence.RequeueAsync(command)).Outcome);
        var now = DateTimeOffset.UtcNow;
        var claimedGeneration = await persistence.TryClaimAsync(
            job.Id,
            "worker-new",
            now,
            now.AddMinutes(5));
        Assert.NotNull(claimedGeneration);

        var result = await persistence.RequeueAsync(command with
        {
            ExpectedStatus = MoveJobStatus.Queued
        });

        Assert.Equal(MoveRequeueOutcome.StaleState, result.Outcome);
        var persisted = await persistence.GetByIdAsync(job.Id);
        Assert.Equal(MoveJobStatus.Running, persisted!.Status);
        Assert.Equal("worker-new", persisted.LeaseOwner);
        Assert.Equal(claimedGeneration, persisted.LeaseGeneration);
        Assert.Equal("v3:move:42:s:stale-requeue-new", persisted.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task RequeueAsync_DeduplicationCollision_ReturnsConflictingActiveJob()
    {
        var persistence = CreatePersistence();
        var failed = CreateJob("v3:move:42:s:failed-original");
        failed.Status = MoveJobStatus.Failed;
        failed.ActiveDeduplicationKey = null;
        var active = CreateJob("v3:move:42:s:collision");
        await persistence.AddAsync(failed);
        await persistence.AddAsync(active);

        var result = await persistence.RequeueAsync(CreateRequeueCommand(
            failed,
            "v3:move:42:s:collision"));

        Assert.Equal(MoveRequeueOutcome.ConflictingActiveJob, result.Outcome);
        Assert.Equal(active.Id, result.Job?.Id);
        Assert.Equal(MoveJobStatus.Failed, (await persistence.GetByIdAsync(failed.Id))?.Status);
    }

    [Fact]
    public async Task RequeueAsync_MatchingQueuedRepair_ReturnsIdempotentOutcome()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v3:move:42:s:matching-repair");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(job, "v3:move:42:s:matching-repair-new");
        var first = await persistence.RequeueAsync(command);
        var second = await persistence.RequeueAsync(command with
        {
            ExpectedStatus = MoveJobStatus.Queued
        });

        Assert.Equal(MoveRequeueOutcome.Requeued, first.Outcome);
        Assert.Equal(MoveRequeueOutcome.AlreadyQueuedWithMatchingIdentity, second.Outcome);
        Assert.Equal(job.Id, second.Job?.Id);
    }

    [Fact]
    public async Task RequeueAsync_ConcurrentRequests_OnlyOneExpectedStateTransitionWins()
    {
        var persistence = CreatePersistence();
        var job = CreateJob("v3:move:42:s:concurrent-requeue");
        job.Status = MoveJobStatus.Failed;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var command = CreateRequeueCommand(job, "v3:move:42:s:concurrent-requeue-new");

        var results = await Task.WhenAll(
            persistence.RequeueAsync(command),
            persistence.RequeueAsync(command));

        Assert.Single(results, result => result.Outcome == MoveRequeueOutcome.Requeued);
        Assert.Single(results, result => result.Outcome == MoveRequeueOutcome.StaleState);
        Assert.Equal(MoveJobStatus.Queued, (await persistence.GetByIdAsync(job.Id))?.Status);
    }

    [Theory]
    [InlineData(
        FileSystemPathSyntax.Windows,
        FileSystemCaseSensitivity.Sensitive,
        FileSystemCaseSensitivityMode.Sensitive,
        @"\\server\downloads\Book",
        @"\\server\library\Book",
        @"\\server\downloads",
        @"\\server\library")]
    [InlineData(
        FileSystemPathSyntax.Windows,
        FileSystemCaseSensitivity.Insensitive,
        FileSystemCaseSensitivityMode.Insensitive,
        @"C:\Downloads\Book",
        @"C:\Library\Book",
        @"C:\Downloads",
        @"C:\Library")]
    [InlineData(
        FileSystemPathSyntax.Unix,
        FileSystemCaseSensitivity.Sensitive,
        FileSystemCaseSensitivityMode.Sensitive,
        "/downloads/Author Name/Book",
        "/library/Author Name/Book",
        "/downloads",
        "/library")]
    [InlineData(
        FileSystemPathSyntax.Unix,
        FileSystemCaseSensitivity.Insensitive,
        FileSystemCaseSensitivityMode.Insensitive,
        "/mnt/Downloads/Book",
        "/mnt/Library/Book",
        "/mnt/Downloads",
        "/mnt/Library")]
    public async Task RequeueAsync_PersistsExplicitEndpointSemantics(
        FileSystemPathSyntax syntax,
        FileSystemCaseSensitivity sensitivity,
        FileSystemCaseSensitivityMode requestedMode,
        string sourcePath,
        string targetPath,
        string sourceBoundary,
        string targetBoundary)
    {
        var persistence = CreatePersistence();
        var job = CreateJob($"legacy:{Guid.NewGuid():N}");
        job.Status = MoveJobStatus.NeedsAttention;
        job.ActiveDeduplicationKey = null;
        await persistence.AddAsync(job);
        var sourceIdentity = new PathIdentitySnapshot(
            syntax,
            sensitivity,
            requestedMode,
            sourceBoundary);
        var targetIdentity = new PathIdentitySnapshot(
            syntax,
            sensitivity,
            requestedMode,
            targetBoundary);

        var result = await persistence.RequeueAsync(new RequeueMoveCommand(
            job.Id,
            MoveJobStatus.NeedsAttention,
            sourcePath,
            sourceIdentity,
            targetPath,
            targetIdentity,
            $"v3:move:42:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow));

        Assert.Equal(MoveRequeueOutcome.Requeued, result.Outcome);
        Assert.Equal(syntax, result.Job?.SourcePathSyntax);
        Assert.Equal(syntax, result.Job?.TargetPathSyntax);
        Assert.Equal(sensitivity, result.Job?.SourceCaseSensitivity);
        Assert.Equal(sensitivity, result.Job?.TargetCaseSensitivity);
        Assert.Equal(requestedMode, result.Job?.SourceCaseSensitivityMode);
        Assert.Equal(requestedMode, result.Job?.TargetCaseSensitivityMode);
    }

    private EfMoveQueuePersistence CreatePersistence(IFileSystemSemanticsResolver? resolver = null) =>
        new(_factory, resolver ?? BuildSemanticsResolver());

    private static IFileSystemSemanticsResolver BuildSemanticsResolver(
        Func<string, Exception?>? exceptionFactory = null)
    {
        var resolver = new Mock<IFileSystemSemanticsResolver>();
        resolver.Setup(service => service.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
            {
                if (exceptionFactory?.Invoke(path) is { } exception)
                {
                    throw exception;
                }

                return ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    path));
            });
        return resolver.Object;
    }

    private static MoveJob CreateJob(string key) => new()
    {
        AudiobookId = 42,
        RequestedPath = "/library/book",
        Status = MoveJobStatus.Queued,
        ActiveDeduplicationKey = key
    };

    private static RequeueMoveCommand CreateRequeueCommand(
        MoveJob job,
        string key)
    {
        var sourcePath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/downloads/book");
        var targetPath = FileSystemPathIdentity.ResolveNativeAbsolutePath("/library/book");
        var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        var sourceIdentity = new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            sourcePath);
        var targetIdentity = new PathIdentitySnapshot(
            syntax,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            targetPath);
        return new RequeueMoveCommand(
            job.Id,
            job.Status,
            sourcePath,
            sourceIdentity,
            targetPath,
            targetIdentity,
            key,
            DateTimeOffset.UtcNow);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
