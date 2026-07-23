using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_SourceRootReplacedBeforeQuarantineMove_PreservesExternalFile()
    {
        var capabilityParent = FileService.GetTempDirectory("content-move-cleanup-race-capability");
        var capabilityTarget = FileService.GetTempDirectory("content-move-cleanup-race-capability-target");
        var capabilityLink = Path.Join(capabilityParent, "link");
        if (!TryCreateDirectoryLink(capabilityLink, capabilityTarget))
        {
            return;
        }

        TryRemoveDirectoryLink(capabilityLink);
        var source = FileService.GetTempDirectory("content-move-cleanup-race-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var sourceBackup = source + $"-backup-{Guid.NewGuid():N}";
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-cleanup-race-dst-{Guid.NewGuid():N}");
        var external = FileService.GetTempDirectory("content-move-cleanup-race-external");
        var externalFile = await FileService.GetFileAsync(
            external,
            "book.m4b",
            "verified audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var injector = new ReplaceSourceRootBeforeCleanupMove(
            source,
            sourceBackup,
            external);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        try
        {
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
            Assert.True(File.Exists(Path.Join(sourceBackup, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }
        finally
        {
            TryRemoveDirectoryLink(source);
            if (Directory.Exists(sourceBackup) && !Directory.Exists(source))
            {
                Directory.Move(sourceBackup, source);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_NestedSourceParentReplacedAfterRevalidation_DoesNotConsumeExternalFile()
    {
        var capabilityParent = FileService.GetTempDirectory("content-move-nested-cleanup-race-capability");
        var capabilityTarget = FileService.GetTempDirectory("content-move-nested-cleanup-race-capability-target");
        var capabilityLink = Path.Join(capabilityParent, "link");
        if (!TryCreateDirectoryLink(capabilityLink, capabilityTarget))
        {
            return;
        }

        TryRemoveDirectoryLink(capabilityLink);
        var source = FileService.GetTempDirectory("content-move-nested-cleanup-race-src");
        var nestedSource = Path.Join(source, "extras");
        Directory.CreateDirectory(nestedSource);
        var sourceFile = await FileService.GetFileAsync(
            nestedSource,
            "book.m4b",
            "verified audio");
        var nestedSourceBackup = nestedSource + $"-backup-{Guid.NewGuid():N}";
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-nested-cleanup-race-dst-{Guid.NewGuid():N}");
        var external = FileService.GetTempDirectory("content-move-nested-cleanup-race-external");
        var externalFile = await FileService.GetFileAsync(
            external,
            "book.m4b",
            "verified audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ReplaceNestedSourceParentAfterRevalidation(
                nestedSource,
                nestedSourceBackup,
                external));

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
            var displacedSourceFile = Path.Join(nestedSourceBackup, "book.m4b");
            Assert.True(File.Exists(sourceFile) || File.Exists(displacedSourceFile));
            Assert.Equal(
                "verified audio",
                await File.ReadAllTextAsync(
                    File.Exists(sourceFile) ? sourceFile : displacedSourceFile));
            Assert.True(File.Exists(Path.Join(target, "extras", "book.m4b")));
        }
        finally
        {
            TryRemoveDirectoryLink(nestedSource);
            if (Directory.Exists(nestedSourceBackup) && !Directory.Exists(nestedSource))
            {
                Directory.Move(nestedSourceBackup, nestedSource);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_UnownedTargetEntryAppearsBeforeSourceMove_PreservesSource()
    {
        var source = FileService.GetTempDirectory("content-move-unowned-target-cleanup-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-unowned-target-cleanup-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new AddUnownedTargetEntryBeforeSourceMove(target));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("unowned file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.Equal(
            "preserve me",
            await File.ReadAllTextAsync(Path.Join(target, "operator-note.txt")));
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{request.JobId:N}");
        Assert.False(File.Exists(Path.Join(quarantineRoot, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_QuarantineFileReplacedAfterRevalidation_DoesNotDeleteExternalBytes()
    {
        var source = FileService.GetTempDirectory("content-move-quarantine-file-race-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-quarantine-file-race-dst-{Guid.NewGuid():N}");
        var external = FileService.GetTempDirectory("content-move-quarantine-file-race-external");
        var externalFile = await FileService.GetFileAsync(
            external,
            "external.m4b",
            "external bytes");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{request.JobId:N}");
        var quarantineFile = Path.Join(quarantineRoot, "book.m4b");
        var quarantineBackup = Path.Join(quarantineRoot, "book.original.m4b");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ReplaceQuarantineFileAfterRevalidation(
                quarantineFile,
                quarantineBackup,
                externalFile));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(File.Exists(quarantineBackup));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(quarantineBackup));
        var preservedExternalPath = File.Exists(externalFile)
            ? externalFile
            : quarantineFile;
        Assert.True(File.Exists(preservedExternalPath));
        Assert.Equal("external bytes", await File.ReadAllTextAsync(preservedExternalPath));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_TargetFileReplacedBeforeQuarantineDelete_PreservesQuarantineAndExternalFile()
    {
        var capabilityRoot = FileService.GetTempDirectory("content-move-target-race-capability");
        var capabilityTarget = await FileService.GetFileAsync(
            capabilityRoot,
            "target.bin",
            "capability");
        var capabilityLink = Path.Join(capabilityRoot, "link.bin");
        try
        {
            File.CreateSymbolicLink(capabilityLink, capabilityTarget);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        File.Delete(capabilityLink);
        var source = FileService.GetTempDirectory("content-move-target-race-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-target-race-dst-{Guid.NewGuid():N}");
        var external = FileService.GetTempDirectory("content-move-target-race-external");
        var externalFile = await FileService.GetFileAsync(
            external,
            "book.m4b",
            "verified audio");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var injector = new ReplaceTargetFileBeforeQuarantineDelete(
            Path.Join(target, "book.m4b"),
            externalFile);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        try
        {
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            var quarantineRoot = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{request.JobId:N}");
            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
            Assert.True(File.Exists(Path.Join(quarantineRoot, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }
        finally
        {
            var linkedTargetFile = Path.Join(target, "book.m4b");
            if (File.Exists(linkedTargetFile))
            {
                File.Delete(linkedTargetFile);
            }
        }
    }

    private sealed class ReplaceNestedSourceParentAfterRevalidation(
        string nestedSource,
        string nestedSourceBackup,
        string external) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (!_replaced
                && faultPoint == SourceCleanupFaultPoint.BeforeSourceFilePublication)
            {
                Directory.Move(nestedSource, nestedSourceBackup);
                if (!TryCreateDirectoryLink(nestedSource, external))
                {
                    throw new IOException("The nested source replacement link could not be created.");
                }

                _replaced = true;
                return;
            }

            if (_replaced && faultPoint == SourceCleanupFaultPoint.BeforeQuarantineFileDelete)
            {
                throw new IOException("Stop after quarantine publication for inspection.");
            }
        }
    }

    private sealed class AddUnownedTargetEntryBeforeSourceMove(
        string target) : IMoveFaultInjector
    {
        private bool _added;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (_added || faultPoint != SourceCleanupFaultPoint.BeforeSourceFileMove)
            {
                return;
            }

            File.WriteAllText(Path.Join(target, "operator-note.txt"), "preserve me");
            _added = true;
        }
    }

    private sealed class ReplaceQuarantineFileAfterRevalidation(
        string quarantineFile,
        string quarantineBackup,
        string externalFile) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != SourceCleanupFaultPoint.BeforeQuarantineFileRemoval)
            {
                return;
            }

            File.Move(quarantineFile, quarantineBackup);
            File.Move(externalFile, quarantineFile);
            _replaced = true;
        }
    }

    private sealed class ReplaceTargetFileBeforeQuarantineDelete(
        string targetFile,
        string externalFile) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != SourceCleanupFaultPoint.BeforeQuarantineFileDelete)
            {
                return;
            }

            File.Delete(targetFile);
            File.CreateSymbolicLink(targetFile, externalFile);
            _replaced = true;
        }
    }

    private sealed class ReplaceSourceRootBeforeCleanupMove(
        string source,
        string sourceBackup,
        string external) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != SourceCleanupFaultPoint.BeforeSourceFileMove)
            {
                return;
            }

            Directory.Move(source, sourceBackup);
            if (!TryCreateDirectoryLink(source, external))
            {
                throw new IOException("The source replacement link could not be created.");
            }

            _replaced = true;
        }
    }
}
