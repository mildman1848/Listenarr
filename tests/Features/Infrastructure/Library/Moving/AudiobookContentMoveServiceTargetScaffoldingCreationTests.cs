using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_ScaffoldParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("content-move-scaffold-parent-race-root");
        var scaffoldParent = Path.Join(root, "library");
        var displacedParent = Path.Join(root, "library.original");
        var external = FileService.GetTempDirectory("content-move-scaffold-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(scaffoldParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-scaffold-parent-race-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(scaffoldParent, "Author", "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var temporaryRoot = Path.Join(
            scaffoldParent,
            $".listenarr-scaffold-{request.JobId:N}");
        var hookRan = false;
        var temporaryRootAlreadyExisted = false;
        void ReplaceParent(string path)
        {
            if (hookRan || !string.Equals(path, temporaryRoot, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            temporaryRootAlreadyExisted = Directory.Exists(temporaryRoot);
            Directory.Move(scaffoldParent, displacedParent);
            Directory.CreateSymbolicLink(scaffoldParent, external);
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
            Assert.False(temporaryRootAlreadyExisted);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.True(File.Exists(sourceFile));
        }
        finally
        {
            TryDeleteTempDirectoryLink(scaffoldParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(scaffoldParent))
            {
                Directory.Move(displacedParent, scaffoldParent);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_UnownedPreparedScaffold_IsNotAdoptedOrMarked()
    {
        var scaffoldParent = FileService.GetTempDirectory("content-move-unowned-prepared-scaffold-root");
        var source = FileService.GetTempDirectory("content-move-unowned-prepared-scaffold-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(scaffoldParent, "Author", "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var temporaryRoot = Path.Join(
            scaffoldParent,
            $".listenarr-scaffold-{request.JobId:N}");
        Directory.CreateDirectory(temporaryRoot);

        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new DisableAtomicRename());

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(Directory.Exists(temporaryRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryRoot));
        Assert.False(File.Exists(Path.Join(temporaryRoot, ".listenarr-scaffold-owner.json")));
        Assert.True(File.Exists(sourceFile));
        Assert.False(Directory.Exists(Path.Join(scaffoldParent, "Author")));
    }

    [Fact]
    public async Task MoveContentsAsync_ScaffoldParentReplacedAtPublication_DoesNotPublishSubstituteTree()
    {
        var root = FileService.GetTempDirectory("content-move-scaffold-publication-race-root");
        var scaffoldParent = Path.Join(root, "library");
        var displacedParent = Path.Join(root, "library.original");
        var external = FileService.GetTempDirectory("content-move-scaffold-publication-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(scaffoldParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-scaffold-publication-race-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(scaffoldParent, "Author", "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var temporaryName = $".listenarr-scaffold-{request.JobId:N}";
        var temporaryRoot = Path.Join(scaffoldParent, temporaryName);
        var externalTemporaryRoot = Path.Join(external, temporaryName);
        var externalPublishedRoot = Path.Join(external, "Author");
        var publicationRan = false;
        var substitutePublished = false;
        void ReplaceParentAndSubstitutePreparedTree()
        {
            publicationRan = true;
            Directory.Move(scaffoldParent, displacedParent);
            if (!TryCreateTempDirectoryJunction(scaffoldParent, external))
            {
                throw new IOException("Could not create the scaffold parent replacement junction.");
            }
            Directory.CreateDirectory(externalTemporaryRoot);
            File.Copy(
                Path.Join(displacedParent, temporaryName, ".listenarr-scaffold-owner.json"),
                Path.Join(externalTemporaryRoot, ".listenarr-scaffold-owner.json"));
        }

        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceScaffoldParentAtPublication(
                    ReplaceParentAndSubstitutePreparedTree,
                    () => substitutePublished = Directory.Exists(externalPublishedRoot)));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(publicationRan);
            Assert.False(substitutePublished);
            Assert.False(Directory.Exists(externalPublishedRoot));
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(Path.Join(external, "Author", "Book")));
        }
        finally
        {
            TryDeleteTempDirectoryLink(scaffoldParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(scaffoldParent))
            {
                Directory.Move(displacedParent, scaffoldParent);
            }
        }
    }

    private sealed class ReplaceScaffoldParentAtPublication(
        Action replaceParent,
        Action observePublishedSubstitute) : IMoveFaultInjector
    {
        private bool _replaced;
        private bool _observed;

        public bool AllowAtomicRename => false;

        public void OnTargetScaffoldPreparation(
            Guid jobId,
            TargetScaffoldPreparationFaultPoint faultPoint)
        {
            if (!_replaced && faultPoint == TargetScaffoldPreparationFaultPoint.BeforePublication)
            {
                _replaced = true;
                replaceParent();
                return;
            }

            if (_observed || faultPoint != TargetScaffoldPreparationFaultPoint.AfterPublication)
            {
                return;
            }

            _observed = true;
            observePublishedSubstitute();
            throw new IOException("Stop after observing target scaffold publication.");
        }
    }
}
