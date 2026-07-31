using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_ExistingLinkedTarget_DoesNotWriteExternalRecoveryMarker()
    {
        var source = FileService.GetTempDirectory("content-move-linked-target-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var externalTarget = FileService.GetTempDirectory("content-move-linked-target-external");
        var linkParent = FileService.GetTempDirectory("content-move-linked-target-parent");
        var targetLink = Path.Join(linkParent, "linked-target");
        Assert.True(
            TryCreateDirectoryLink(targetLink, externalTarget),
            "The required directory link could not be created.");

        try
        {
            var request = await CreateLeasedMoveRequestAsync(source, targetLink);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(externalTarget));
        }
        finally
        {
            TryRemoveDirectoryLink(targetLink);
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_WindowsTargetJunction_DoesNotWriteExternalRecoveryMarker()
    {

        var source = FileService.GetTempDirectory("content-move-target-junction-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var externalTarget = FileService.GetTempDirectory("content-move-target-junction-external");
        var linkParent = FileService.GetTempDirectory("content-move-target-junction-parent");
        var targetJunction = Path.Join(linkParent, "junction-target");
        Assert.True(
            TryCreateWindowsJunction(targetJunction, externalTarget),
            "The required Windows junction could not be created.");

        try
        {
            var request = await CreateLeasedMoveRequestAsync(source, targetJunction);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(externalTarget));
        }
        finally
        {
            TryRemoveDirectoryLink(targetJunction);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_LinkedSource_PreservesExternalOrphanMarkerWriteFile()
    {
        var externalSource = FileService.GetTempDirectory("content-move-orphan-linked-source-external");
        var sourceFile = await FileService.GetFileAsync(externalSource, "book.m4b", "audio");
        var linkParent = FileService.GetTempDirectory("content-move-orphan-linked-source-parent");
        var sourceLink = Path.Join(linkParent, "linked-source");
        Assert.True(
            TryCreateDirectoryLink(sourceLink, externalSource),
            "The required directory link could not be created.");

        try
        {
            var target = Path.Join(linkParent, $"target-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(sourceLink, target, jobId);
            var orphanPath = Path.Join(
                externalSource,
                $".listenarr-move-{jobId:N}.pending.writing-{Guid.NewGuid():N}");
            await WriteRecoveryMarkerPayloadAsync(orphanPath, jobId, sourceLink, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(orphanPath));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(sourceLink);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_LinkedTarget_PreservesExternalOrphanMarkerWriteFile()
    {
        var source = FileService.GetTempDirectory("content-move-orphan-linked-target-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var externalTarget = FileService.GetTempDirectory("content-move-orphan-linked-target-external");
        var linkParent = FileService.GetTempDirectory("content-move-orphan-linked-target-parent");
        var targetLink = Path.Join(linkParent, "linked-target");
        Assert.True(
            TryCreateDirectoryLink(targetLink, externalTarget),
            "The required directory link could not be created.");

        try
        {
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, targetLink, jobId);
            var orphanPath = Path.Join(
                externalTarget,
                $".listenarr-move-{jobId:N}.pending.writing-{Guid.NewGuid():N}");
            await WriteRecoveryMarkerPayloadAsync(orphanPath, jobId, source, targetLink);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(orphanPath));
        }
        finally
        {
            TryRemoveDirectoryLink(targetLink);
        }
    }

    private static Task WriteRecoveryMarkerPayloadAsync(
        string path,
        Guid jobId,
        string source,
        string target)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            JobId = jobId,
            Source = Path.GetFullPath(source),
            Target = Path.GetFullPath(target),
            Stage = "copy-started"
        });
        return File.WriteAllTextAsync(path, payload);
    }
}
