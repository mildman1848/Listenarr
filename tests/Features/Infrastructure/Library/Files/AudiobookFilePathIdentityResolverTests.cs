using Listenarr.Infrastructure.Library.Files;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Files;

[Trait("Name", "AudiobookFilePathIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookFilePathIdentityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_WindowsInsensitiveVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "C:\\Library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "C:\\Library\\Author\\Book" };
        var first = await resolver.ResolveAsync(audiobook, "C:\\Library\\Author\\Book\\Disc 1\\Book.m4b");
        var second = await resolver.ResolveAsync(audiobook, "c:/LIBRARY/Author/Book/Disc 1/BOOK.m4b");
        Assert.Equal(PathIdentityState.Valid, first.State);
        Assert.Equal(first.OwnershipKey, second.OwnershipKey);
        Assert.Equal(first.LookupKey, second.LookupKey);
        Assert.Equal(FileSystemPathSyntax.Windows, first.Syntax);
        Assert.Equal(FileSystemCaseSensitivity.Insensitive, first.CaseSensitivity);
        Assert.Equal("C:\\Library", first.BoundaryPath);
    }

    [Fact]
    public async Task ResolveAsync_UnixSensitiveCaseVariants_CreateDifferentOwnershipIdentities()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "/library/author/book" };
        var upper = await resolver.ResolveAsync(audiobook, "/library/author/book/Book.m4b");
        var lower = await resolver.ResolveAsync(audiobook, "/library/author/book/book.m4b");
        Assert.NotEqual(upper.OwnershipKey, lower.OwnershipKey);
        Assert.Equal(upper.LookupKey, lower.LookupKey);
    }

    [Fact]
    public async Task ResolveAsync_UnixConfiguredInsensitiveCaseVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "/library/author/book" };
        var upper = await resolver.ResolveAsync(audiobook, "/library/author/book/Book.m4b");
        var lower = await resolver.ResolveAsync(audiobook, "/library/author/book/book.m4b");
        Assert.Equal(upper.OwnershipKey, lower.OwnershipKey);
    }

    [Fact]
    public async Task ResolveAsync_UncVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "\\\\server\\share", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "\\\\server\\share\\Author\\Book" };
        var first = await resolver.ResolveAsync(audiobook, "\\\\server\\share\\Author\\Book\\Book.m4b");
        var second = await resolver.ResolveAsync(audiobook, "\\\\SERVER\\SHARE\\Author\\Book\\BOOK.m4b");
        Assert.Equal(first.OwnershipKey, second.OwnershipKey);
    }

    [Fact]
    public async Task ResolveAsync_DoubleSlashBaseUsesNativeFilesystemContext()
    {
        var expectedSyntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        var sensitivity = OperatingSystem.IsWindows()
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var root = new RootFolder
        {
            Path = "//server/share",
            CaseSensitivityMode = sensitivity == FileSystemCaseSensitivity.Insensitive
                ? FileSystemCaseSensitivityMode.Insensitive
                : FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = sensitivity,
            PathIdentityState = PathIdentityState.Valid
        };
        var resolver = BuildResolver(root);
        var audiobook = new Audiobook { BasePath = "//server/share/Author/Book" };

        var identity = await resolver.ResolveAsync(audiobook, "Disc 1/Book.m4b");

        Assert.Equal(PathIdentityState.Valid, identity.State);
        Assert.Equal(expectedSyntax, identity.Syntax);
        Assert.Equal(
            FileSystemPathIdentity.Canonicalize(
                "//server/share/Author/Book/Disc 1/Book.m4b",
                expectedSyntax),
            identity.CanonicalPath);
    }

    [Fact]
    public async Task ResolveAsync_SameRelativePathUnderDifferentBases_IsDistinct()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive, PathIdentityState = PathIdentityState.Valid });
        var first = await resolver.ResolveAsync(new Audiobook { BasePath = "/library/first" }, "book.m4b");
        var second = await resolver.ResolveAsync(new Audiobook { BasePath = "/library/second" }, "book.m4b");
        Assert.NotEqual(first.OwnershipKey, second.OwnershipKey);
        Assert.Equal("/library/first/book.m4b", first.CanonicalPath);
        Assert.Equal("/library/second/book.m4b", second.CanonicalPath);
    }

    [Fact]
    public async Task ResolveAsync_MultipleFiles_LoadsRootFoldersOncePerResolverScope()
    {
        var root = new RootFolder
        {
            Path = "/library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
            PathIdentityState = PathIdentityState.Valid
        };
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        var resolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict).Object);
        var audiobook = new Audiobook { BasePath = "/library/author/book" };

        await resolver.ResolveAsync(audiobook, "first.m4b");
        await resolver.ResolveAsync(audiobook, "second.m4b");

        roots.Verify(repository => repository.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_UnavailableFilesystemSemantics_ReturnsUnavailableIdentity()
    {
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semantics.Setup(resolver => resolver.ResolveAsync("/offline/library/book.m4b", FileSystemCaseSensitivityMode.Auto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(new FileSystemPathSemantics(FileSystemPathSyntax.Unix, FileSystemCaseSensitivity.Unknown), PathIdentityState.Unavailable, "/offline/library", "Network path is unavailable."));
        var resolver = new AudiobookFilePathIdentityResolver(roots.Object, semantics.Object);
        var identity = await resolver.ResolveAsync(new Audiobook { BasePath = "/offline/library" }, "book.m4b");
        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Null(identity.OwnershipKey);
        Assert.NotNull(identity.LookupKey);
    }

    private static AudiobookFilePathIdentityResolver BuildResolver(RootFolder root)
    {
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        return new AudiobookFilePathIdentityResolver(roots.Object, new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict).Object);
    }
}
