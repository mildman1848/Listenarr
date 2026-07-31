using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_QuarantineParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("content-move-quarantine-parent-race-root");
        var sourceParent = Path.Join(root, "source-parent");
        var displacedParent = Path.Join(root, "source-parent.original");
        var external = FileService.GetTempDirectory("content-move-quarantine-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(sourceParent);
        Assert.True(
            TryCreateTempDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        var source = Path.Join(sourceParent, "Book");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempDirectory("content-move-quarantine-parent-race-target"),
            "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var hookRan = false;
        var quarantineAlreadyExistedAtHook = false;
        void ReplaceParent(string path)
        {
            if (hookRan || !string.Equals(path, quarantineRoot, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            quarantineAlreadyExistedAtHook = Directory.Exists(quarantineRoot);
            Directory.Move(sourceParent, displacedParent);
            Directory.CreateSymbolicLink(sourceParent, external);
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(ReplaceParent);
        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new DisableAtomicRename());
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(hookRan);
            Assert.False(quarantineAlreadyExistedAtHook);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.True(File.Exists(Path.Join(
                displacedParent,
                Path.GetFileName(source),
                "book.m4b")));
        }
        finally
        {
            TryDeleteTempDirectoryLink(sourceParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(sourceParent))
            {
                Directory.Move(displacedParent, sourceParent);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_QuarantineParentReplacedBeforeMarkerCreation_DoesNotWriteOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("content-move-quarantine-marker-race-root");
        var sourceParent = Path.Join(root, "source-parent");
        var displacedParent = Path.Join(root, "source-parent.original");
        var external = FileService.GetTempDirectory("content-move-quarantine-marker-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(sourceParent);
        Assert.True(
            TryCreateTempDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        var source = Path.Join(sourceParent, "Book");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempDirectory("content-move-quarantine-marker-race-target"),
            "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var externalQuarantine = Path.Join(external, Path.GetFileName(quarantineRoot));
        var replacementRan = false;
        void ReplaceParent()
        {
            replacementRan = true;
            Directory.CreateDirectory(externalQuarantine);
            Directory.Move(sourceParent, displacedParent);
            Directory.CreateSymbolicLink(sourceParent, external);
        }

        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceQuarantineParentBeforeMarkerCreation(ReplaceParent));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(replacementRan);
            Assert.False(File.Exists(Path.Join(
                externalQuarantine,
                ".listenarr-quarantine-owner.json")));
            Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }
        finally
        {
            TryDeleteTempDirectoryLink(sourceParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(sourceParent))
            {
                Directory.Move(displacedParent, sourceParent);
            }
        }
    }

    private sealed class ReplaceQuarantineParentBeforeMarkerCreation(Action replaceParent)
        : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || markerKind != OwnershipMarkerKind.QuarantineDirectory
                || faultPoint != OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation)
            {
                return;
            }

            _replaced = true;
            replaceParent();
        }
    }
}
