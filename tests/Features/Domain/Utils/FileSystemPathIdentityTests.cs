namespace Listenarr.Tests.Features.Domain.Utils;

public sealed class FileSystemPathIdentityTests
{
    [Fact]
    public void UnixIdentity_PreservesLiteralBackslash()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        Assert.False(FileSystemPathIdentity.AreEquivalent(
            "/books/Author\\Title",
            "/books/Author/Title",
            semantics));
    }

    [Fact]
    public void UnixRoot_ContainsAbsoluteChild()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        Assert.True(FileSystemPathIdentity.IsSameOrInside("/Author/Title", "/", semantics));
    }

    [Fact]
    public void InsensitiveUnixVolume_UsesFilesystemSemantics()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.AreEquivalent(
            "/Volumes/Books/Title",
            "/volumes/books/title/",
            semantics));
    }

    [Fact]
    public void WindowsIdentity_NormalizesSeparatorsAndCase()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.AreEquivalent(
            @"C:\Books\Author\Title",
            "c:/books/author/title/",
            semantics));
    }

    [Fact]
    public void ResolveRelativePath_UnixBackslashRemainsInFilename()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        var resolved = FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            "/target",
            "Author\\Title/book.m4b",
            semantics,
            out var path);

        Assert.True(resolved);
        Assert.Equal("/target/Author\\Title/book.m4b", path);
    }

    [Fact]
    public void GetRelativePath_UsesResolvedCaseSemantics()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        var resolved = FileSystemPathIdentity.TryGetRelativePathWithinBase(
            "/books",
            "/Books/Author/Title",
            semantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("Author/Title", relativePath);
    }

    [Theory]
    [InlineData(@"Author\Title\book.m4b", FileSystemPathSyntax.Windows, FileSystemPathSyntax.Unix, "Author/Title/book.m4b")]
    [InlineData("Author/Title/book.m4b", FileSystemPathSyntax.Unix, FileSystemPathSyntax.Windows, @"Author\Title\book.m4b")]
    public void ConvertRelativePathSyntax_UsesTargetSeparators(
        string relativePath,
        FileSystemPathSyntax sourceSyntax,
        FileSystemPathSyntax targetSyntax,
        string expected)
    {
        Assert.Equal(
            expected,
            FileSystemPathIdentity.ConvertRelativePathSyntax(relativePath, sourceSyntax, targetSyntax));
    }

    [Fact]
    public void IdentityKey_IsVersionedAndStableForEquivalentPaths()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        var first = FileSystemPathIdentity.CreateKey(
            "move:7",
            @"C:\Books\Title",
            semantics);
        var second = FileSystemPathIdentity.CreateKey(
            "move:7",
            "c:/books/title/",
            semantics);

        Assert.StartsWith("v2:move:7:i:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void EquivalentEndpoints_EitherInsensitiveIdentityMakesCaseOnlyVariantEquivalent()
    {
        var source = "/library/Book";
        var target = "/library/book/";
        var sourceIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library");
        var targetIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            "/library");

        Assert.True(FileSystemPathIdentity.AreEquivalentEndpoints(
            source,
            sourceIdentity,
            target,
            targetIdentity));
    }

    [Fact]
    public void EquivalentEndpoints_BothSensitiveIdentitiesPreserveCaseOnlyDifference()
    {
        var source = "/library/Book";
        var target = "/library/book";
        var identity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library");

        Assert.False(FileSystemPathIdentity.AreEquivalentEndpoints(
            source,
            identity,
            target,
            identity));
    }

    [Fact]
    public void EquivalentEndpoints_DifferentSyntaxesAreDistinct()
    {
        var sourceIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            @"C:\Library");
        var targetIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            "/c/Library");

        Assert.False(FileSystemPathIdentity.AreEquivalentEndpoints(
            @"C:\Library\Book",
            sourceIdentity,
            "/c/Library/Book",
            targetIdentity));
    }

    [Fact]
    public void UnknownSensitivity_CannotCreateIdentityKey()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Unknown);

        Assert.Throws<InvalidOperationException>(() =>
            FileSystemPathIdentity.CreateKey("root", "/books", semantics));
    }
}
