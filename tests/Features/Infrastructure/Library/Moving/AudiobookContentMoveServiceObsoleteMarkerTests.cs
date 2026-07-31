using System.Text.Json;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Theory]
    [InlineData("copy-started")]
    [InlineData("copy-complete")]
    [InlineData("source-cleanup-complete")]
    [InlineData("atomic-rename-complete")]
    public async Task GetRecoverableMoveAsync_ObsoleteMarker_PreservesAllFilesystemState(
        string stage)
    {
        var source = FileService.GetTempDirectory($"content-move-obsolete-{stage}-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = FileService.GetTempDirectory($"content-move-obsolete-{stage}-dst");
        var targetFile = await FileService.GetFileAsync(target, "book.m4b", "target audio");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        await File.WriteAllTextAsync(markerPath, stage);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));

        Assert.Contains("obsolete pre-release", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("source audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("target audio", await File.ReadAllTextAsync(targetFile));
        Assert.Equal(stage, await File.ReadAllTextAsync(markerPath));
    }

    [Fact]
    public async Task MoveContentsAsync_MatchingJobShapedPartialWithoutMarker_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-unmarked-partial-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-unmarked-partial-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        var partialPath = Path.Join(
            target,
            $"book.m4b.listenarr-{jobId:N}.partial");
        await File.WriteAllTextAsync(partialPath, "verified audio");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("without structured move ownership", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("verified audio", await File.ReadAllTextAsync(partialPath));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
        Assert.False(File.Exists(Path.Join(target, "book.m4b")));
    }

    [FileLinkFact]
    public async Task MoveContentsAsync_LinkedJobShapedPartialWithoutMarker_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-linked-partial-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-linked-partial-dst");
        var external = FileService.GetTempDirectory("content-move-linked-partial-external");
        var externalFile = await FileService.GetFileAsync(external, "partial.bin", "external bytes");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        var partialPath = Path.Join(
            target,
            $"book.m4b.listenarr-{jobId:N}.partial");
        try
        {
            File.CreateSymbolicLink(partialPath, externalFile);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException(
                $"This native filesystem regression requires symbolic-link support: {exception.Message}");
        }

        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Equal("external bytes", await File.ReadAllTextAsync(externalFile));
            Assert.True(File.Exists(partialPath));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_JsonIsLegacyProperty_DoesNotBypassStructuredIdentityValidation()
    {
        var source = FileService.GetTempDirectory("content-move-json-legacy-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = FileService.GetTempDirectory("content-move-json-legacy-dst");
        await FileService.GetFileAsync(target, "book.m4b", "source audio");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = Guid.NewGuid(),
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-complete",
                IsLegacy = true
            }));

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));

        Assert.Contains("different job", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(markerPath));
    }
}
