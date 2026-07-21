using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileSystemSemanticsResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileSystemSemanticsResolverTests : BaseTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("relative\0path")]
    public async Task ResolveAsync_RejectsInvalidOrRelativePathBeforeProbing(string path)
    {
        var resolver = new FileSystemSemanticsResolver();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(path, FileSystemCaseSensitivityMode.Auto));
    }

    [Fact]
    public async Task ExplicitOverride_ResolvesWithoutExistingPath()
    {
        var probes = 0;
        var resolver = new FileSystemSemanticsResolver
        {
            BeforeProbeForTest = _ => probes++
        };
        var missingPath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "books");

        var resolution = await resolver.ResolveAsync(
            missingPath,
            FileSystemCaseSensitivityMode.Sensitive);

        Assert.Equal(FileSystemCaseSensitivity.Sensitive, resolution.Semantics.CaseSensitivity);
        Assert.Equal(PathIdentityState.Valid, resolution.State);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task AutoProbe_RepeatedBoundary_IsProbedIndependently()
    {
        var root = Path.Join(Path.GetTempPath(), "filesystem-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var probes = 0;
        var resolver = new FileSystemSemanticsResolver
        {
            BeforeProbeForTest = boundary =>
            {
                Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(boundary));
                probes++;
            }
        };
        try
        {
            var first = await resolver.ResolveAsync(root, FileSystemCaseSensitivityMode.Auto);
            var second = await resolver.ResolveAsync(root, FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, first.State);
            Assert.Equal(PathIdentityState.Valid, second.State);
            Assert.Equal(2, probes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".listenarr-case-probe-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AutoProbe_ExistingBoundary_ProbesWithinBoundaryAndRemovesProbeFile()
    {
        var root = Path.Join(Path.GetTempPath(), "filesystem-semantics-" + Guid.NewGuid().ToString("N"));
        var boundary = Path.Join(root, "Books");
        Directory.CreateDirectory(boundary);
        var resolver = new FileSystemSemanticsResolver();
        try
        {
            var resolution = await resolver.ResolveAsync(boundary, FileSystemCaseSensitivityMode.Auto);

            Assert.NotEqual(FileSystemCaseSensitivity.Unknown, resolution.Semantics.CaseSensitivity);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Empty(Directory.EnumerateFileSystemEntries(boundary, ".listenarr-case-probe-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AutoProbe_ResolvesAndRemovesProbeFile()
    {
        var root = Path.Join(Path.GetTempPath(), "filesystem-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var resolver = new FileSystemSemanticsResolver();
        try
        {
            var resolution = await resolver.ResolveAsync(
                Path.Join(root, "future", "books"),
                FileSystemCaseSensitivityMode.Auto);

            Assert.NotEqual(FileSystemCaseSensitivity.Unknown, resolution.Semantics.CaseSensitivity);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Empty(Directory.EnumerateFiles(root, ".listenarr-case-probe-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
