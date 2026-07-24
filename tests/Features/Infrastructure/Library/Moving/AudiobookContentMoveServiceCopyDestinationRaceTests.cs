using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_ValidPersistedPartial_DoesNotConsumeSourceDuringPublication()
    {
        var source = FileService.GetTempDirectory("content-move-persisted-partial-source-check");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-persisted-partial-target-check");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(request.JobId, "book.m4b", sourceFile);
        var partial = Path.Join(
            target,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        await File.WriteAllTextAsync(partial, "verified audio");
        await WriteRecoveryMarkerAsync(
            target,
            request.JobId,
            source,
            target,
            "copy-started");
        var stopAfterPublished = new StopAfterPublished(source);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            stopAfterPublished);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(File.Exists(sourceFile));
        Assert.Null(stopAfterPublished.DeleteAccessError);
        Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_CopyParentReplacedBeforePartialCreation_DoesNotWriteExternalPartial()
    {
        var root = FileService.GetTempDirectory("content-move-copy-parent-race-root");
        var targetParent = Path.Join(root, "destination-parent");
        var external = FileService.GetTempDirectory("content-move-copy-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(targetParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-copy-parent-race-source");
        var nestedSource = Path.Join(source, "extras");
        Directory.CreateDirectory(nestedSource);
        var sourceFile = await FileService.GetFileAsync(
            nestedSource,
            "book.m4b",
            "verified audio");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempRoot = Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var destinationParent = Path.Join(tempRoot, "extras");
        var displacedDestinationParent = destinationParent + ".original";
        var externalPartial = Path.Join(
            external,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        var injector = new ReplaceCopyParentBeforePartialCreation(
            destinationParent,
            displacedDestinationParent,
            external);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        try
        {
            await Assert.ThrowsAnyAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(injector.ReplacementRan);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(externalPartial));
        }
        finally
        {
            TryDeleteTempDirectoryLink(destinationParent);
            if (Directory.Exists(displacedDestinationParent)
                && !Directory.Exists(destinationParent))
            {
                Directory.Move(displacedDestinationParent, destinationParent);
            }
        }
    }

    private sealed class StopAfterPublished(string source) : IMoveFaultInjector
    {
        public Exception? DeleteAccessError { get; private set; }

        public Task AfterPublishedAsync(
            Guid jobId,
            CancellationToken cancellationToken)
        {
            try
            {
                using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(source);
                using var sourceEntry = sourceAnchor.OpenExistingFile(
                    "book.m4b",
                    requireDeleteAccess: true);
            }
            catch (Exception exception)
            {
                DeleteAccessError = exception;
            }

            throw new MoveNeedsAttentionException("Stop after publication for source inspection.");
        }
    }

    private sealed class ReplaceCopyParentBeforePartialCreation(
        string destinationParent,
        string displacedDestinationParent,
        string external) : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public bool ReplacementRan => _replaced;

        public void OnCopyMutation(
            Guid jobId,
            CopyMutationFaultPoint faultPoint)
        {
            if (!_replaced && faultPoint == CopyMutationFaultPoint.BeforePartialFileCreation)
            {
                Directory.Move(destinationParent, displacedDestinationParent);
                if (!TryCreateTempDirectoryLink(destinationParent, external))
                {
                    throw new IOException("The copy destination replacement link could not be created.");
                }

                _replaced = true;
                return;
            }

            if (_replaced && faultPoint == CopyMutationFaultPoint.AfterChunkWritten)
            {
                throw new MoveNeedsAttentionException(
                    "Stop after the first copy chunk for external-path inspection.");
            }
        }
    }
}
