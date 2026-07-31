using System.Diagnostics;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_SourceDirectoryLink_BlocksAtomicRename()
    {
        var externalSource = FileService.GetTempDirectory("content-move-root-link-external");
        var externalFile = await FileService.GetFileAsync(externalSource, "book.m4b", "external audio");
        var linkParent = FileService.GetTempDirectory("content-move-root-link-parent");
        var sourceLink = Path.Join(linkParent, "linked-source");
        Assert.True(
            TryCreateDirectoryLink(sourceLink, externalSource),
            "The required directory link could not be created.");

        try
        {
            var target = Path.Join(linkParent, $"target-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(sourceLink, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(Directory.Exists(sourceLink));
            Assert.True(File.Exists(externalFile));
            Assert.Equal("external audio", await File.ReadAllTextAsync(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(sourceLink);
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_WindowsSourceJunction_BlocksAtomicRename()
    {

        var externalSource = FileService.GetTempDirectory("content-move-root-junction-external");
        var externalFile = await FileService.GetFileAsync(externalSource, "book.m4b", "external audio");
        var linkParent = FileService.GetTempDirectory("content-move-root-junction-parent");
        var sourceJunction = Path.Join(linkParent, "junction-source");
        Assert.True(
            TryCreateWindowsJunction(sourceJunction, externalSource),
            "The required Windows junction could not be created.");

        try
        {
            var target = Path.Join(linkParent, $"target-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(sourceJunction, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(Directory.Exists(sourceJunction));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(sourceJunction);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_NestedDirectoryLink_BlocksAtomicRename()
    {
        var source = FileService.GetTempDirectory("content-move-nested-link-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var external = FileService.GetTempDirectory("content-move-nested-link-external");
        var externalFile = await FileService.GetFileAsync(external, "external.txt", "external");
        var nestedLink = Path.Join(source, "linked");
        Assert.True(
            TryCreateDirectoryLink(nestedLink, external),
            "The required directory link could not be created.");

        try
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nested-link-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(nestedLink);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_NestedFileSymlink_BlocksAtomicRename()
    {
        var source = FileService.GetTempDirectory("content-move-file-link-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var external = FileService.GetTempDirectory("content-move-file-link-external");
        var externalFile = await FileService.GetFileAsync(external, "external.txt", "external");
        var linkedFile = Path.Join(source, "linked.txt");
        try
        {
            File.CreateSymbolicLink(linkedFile, externalFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-file-link-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(linkedFile));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (File.Exists(linkedFile))
            {
                File.Delete(linkedFile);
            }
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_AtomicSourceChangesAfterPlanning_DoesNotMoveDirectory()
    {

        var source = FileService.GetTempDirectory("content-move-atomic-drift-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-drift-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new AddAtomicSourceFileBeforeRevalidation(source));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("changed after the atomic move was planned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
        Assert.False(Directory.Exists(target));
    }

    [WindowsFact]
    public async Task MoveContentsAsync_AtomicSourceReplacedAtPublication_DoesNotMoveReplacement()
    {

        var root = FileService.GetTempDirectory("content-move-atomic-publication-race");
        var source = Path.Join(root, "source");
        var displacedSource = Path.Join(root, "source.original");
        var target = Path.Join(root, "target");
        var external = FileService.GetTempDirectory("content-move-atomic-publication-external");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await FileService.GetFileAsync(external, "external.txt", "external");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var injector = new ReplaceAtomicSourceAtPublication(
            source,
            displacedSource,
            external);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(injector.ReplacementRan);
            Assert.True(File.Exists(Path.Join(displacedSource, "book.m4b")));
            Assert.False(Directory.Exists(target));
            Assert.Equal("external", await File.ReadAllTextAsync(Path.Join(external, "external.txt")));
        }
        finally
        {
            TryRemoveDirectoryLink(target);
            TryRemoveDirectoryLink(source);
            if (Directory.Exists(displacedSource) && !Directory.Exists(source))
            {
                Directory.Move(displacedSource, source);
            }
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_AtomicVerificationIoFailure_PreservesRecoverableState()
    {

        var source = FileService.GetTempDirectory("content-move-atomic-verify-retry-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-verify-retry-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ThrowAfterAtomicDirectoryMove());

        await Assert.ThrowsAsync<IOException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        var markerPath = Path.Join(
            target,
            $".listenarr-move-{request.JobId:N}.pending");
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(markerPath));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync(candidate => candidate.Id == request.JobId);
            job.LeaseOwner = "atomic-recovery-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
            request = request with
            {
                LeaseToken = new MoveLeaseToken(job.LeaseOwner, job.LeaseGeneration)
            };
        }

        var recovered = await _provider.GetRequiredService<AudiobookContentMoveService>()
            .GetRecoverableMoveAsync(request, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.True(recovered!.SourceCleanupCompleted);
        Assert.Equal(markerPath, recovered.RecoveryMarkerPath);
    }

    [Fact]
    public async Task MoveContentsAsync_AtomicAccessFailureBeforeRename_FallsBackWithoutStaleMarker()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-access-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-access-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new DenyAtomicRenameBeforeSourceRevalidation());

        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(File.Exists(Path.Join(
            source,
            $".listenarr-move-{request.JobId:N}.pending")));
    }

    [Fact]
    public async Task MoveContentsAsync_NormalSameVolumeSource_UsesAtomicRename()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-normal-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-normal-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(result.RecoveryMarkerPath));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.True(await db.MoveJobEntries.AnyAsync(entry => entry.MoveJobId == jobId));
    }

    private sealed class ReplaceAtomicSourceAtPublication(
        string source,
        string displacedSource,
        string external) : IMoveFaultInjector
    {
        public bool AllowAtomicRename => true;

        public bool ReplacementRan { get; private set; }

        public void OnAtomicRename(Guid jobId, AtomicRenameFaultPoint faultPoint)
        {
            if (ReplacementRan || faultPoint != AtomicRenameFaultPoint.BeforeDirectoryPublication)
            {
                return;
            }

            Directory.Move(source, displacedSource);
            if (!TryCreateWindowsJunction(source, external))
            {
                throw new IOException("The atomic source replacement junction could not be created.");
            }

            ReplacementRan = true;
        }
    }

    private sealed class DenyAtomicRenameBeforeSourceRevalidation : IMoveFaultInjector
    {
        public bool AllowAtomicRename => true;

        public void OnAtomicRename(
            Guid jobId,
            AtomicRenameFaultPoint faultPoint)
        {
            if (faultPoint == AtomicRenameFaultPoint.BeforeSourceRevalidation)
            {
                throw new UnauthorizedAccessException("Simulated atomic rename access denial.");
            }
        }
    }

    private sealed class ThrowAfterAtomicDirectoryMove : IMoveFaultInjector
    {
        public bool AllowAtomicRename => true;

        public void OnAtomicRename(
            Guid jobId,
            AtomicRenameFaultPoint faultPoint)
        {
            if (faultPoint == AtomicRenameFaultPoint.AfterDirectoryMoveBeforeVerification)
            {
                throw new IOException("Simulated transient verification failure.");
            }
        }
    }

    private sealed class AddAtomicSourceFileBeforeRevalidation(
        string source) : IMoveFaultInjector
    {
        public bool AllowAtomicRename => true;

        public void OnAtomicRename(
            Guid jobId,
            AtomicRenameFaultPoint faultPoint)
        {
            if (faultPoint == AtomicRenameFaultPoint.BeforeSourceRevalidation)
            {
                File.WriteAllText(Path.Join(source, "arrived-late.txt"), "new content");
            }
        }
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
            return OperatingSystem.IsWindows()
                && TryCreateWindowsJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
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

    private static void TryRemoveDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to remove test directory link '{linkPath}': {exception.Message}");
        }
    }
}
