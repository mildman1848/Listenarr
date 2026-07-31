using System.Text.Json;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_TempMarkerOwnedByAnotherJob_IsRejectedAndPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-temp-other-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-other-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        var tempName = GetTempMovePath(target, jobId);
        Directory.CreateDirectory(tempName);
        var existingFile = await FileService.GetFileAsync(tempName, "unrelated.txt", "unrelated bytes");
        await WriteTempOwnershipMarkerAsync(
            tempName,
            Guid.NewGuid(),
            source,
            target);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("owned by another job", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("source audio", await File.ReadAllTextAsync(sourceFile));
        Assert.True(Directory.Exists(tempName));
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(existingFile));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task MoveContentsAsync_OwnedTempWithLinkedChild_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-temp-linked-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-linked-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        var tempName = GetTempMovePath(target, jobId);
        Directory.CreateDirectory(tempName);
        await WriteTempOwnershipMarkerAsync(tempName, jobId, source, target);
        var external = FileService.GetTempDirectory("content-move-temp-linked-external");
        var externalFile = await FileService.GetFileAsync(external, "external.txt", "external bytes");
        var linkedChild = Path.Join(tempName, "linked-child");
        Assert.True(
            TryCreateDirectoryLink(linkedChild, external),
            "The required directory link could not be created.");

        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(Directory.Exists(tempName));
            Assert.True(File.Exists(externalFile));
            Assert.Equal("external bytes", await File.ReadAllTextAsync(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(linkedChild);
        }
    }

    [Fact]
    public async Task MoveContentsAsync_ValidOwnedTempDirectory_ResumesPartialCopy()
    {
        var source = FileService.GetTempDirectory("content-move-temp-resume-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-resume-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        var tempName = GetTempMovePath(target, jobId);
        Directory.CreateDirectory(tempName);
        await WriteTempOwnershipMarkerAsync(tempName, jobId, source, target);
        var partialPath = Path.Join(tempName, $"book.m4b.listenarr-{jobId:N}.partial");
        await File.WriteAllTextAsync(partialPath, "verified audio");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(tempName));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        await service.FinalizeMoveAsync(request, result, CancellationToken.None);
        Assert.False(File.Exists(Path.Join(target, ".listenarr-temp-owner.json")));
    }

    [Fact]
    public async Task MoveContentsAsync_ValidOwnedTempDirectory_MayReplaceDifferingCompletedFile()
    {
        var source = FileService.GetTempDirectory("content-move-temp-replace-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-replace-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        var tempName = GetTempMovePath(target, jobId);
        Directory.CreateDirectory(tempName);
        await WriteTempOwnershipMarkerAsync(tempName, jobId, source, target);
        await File.WriteAllTextAsync(Path.Join(tempName, "book.m4b"), "stale bytes");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    private static string GetTempMovePath(string target, Guid jobId)
    {
        var targetParent = Path.GetDirectoryName(target)!;
        return Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + jobId.ToString("N"));
    }

    private static Task WriteTempOwnershipMarkerAsync(
        string directory,
        Guid jobId,
        string source,
        string target)
    {
        return File.WriteAllTextAsync(
            Path.Join(directory, ".listenarr-temp-owner.json"),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                ArtifactType = "temporary-directory",
                JobId = jobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                DirectoryPath = Path.GetFullPath(directory),
                OwnedArtifactType = (string?)null
            }));
    }
}
