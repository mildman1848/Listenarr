using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DirectoryObjectIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryObjectIdentityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_IsStableForSameDirectory()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-stable");
        var resolver = new DirectoryObjectIdentityResolver();

        var first = await resolver.ResolveAsync(directory);
        var second = await resolver.ResolveAsync(directory);

        Assert.True(first.IsAvailable, first.UnavailableReason);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task ResolveAsync_ChangesAfterPathIsRecreated()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-recreated");
        var resolver = new DirectoryObjectIdentityResolver();
        var first = await resolver.ResolveAsync(directory);
        Assert.True(first.IsAvailable, first.UnavailableReason);

        Directory.Delete(directory);
        Directory.CreateDirectory(directory);
        var second = await resolver.ResolveAsync(directory);

        Assert.True(second.IsAvailable, second.UnavailableReason);
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsUnavailableForMissingDirectory()
    {
        var directory = Path.Join(
            FileService.GetTempPath(),
            $"missing-directory-{Guid.NewGuid():N}");
        var resolution = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(directory);

        Assert.False(resolution.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(resolution.UnavailableReason));
    }
}
