using System.Runtime.InteropServices;
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

    [Fact]
    public async Task AutoProbe_PrimaryGenerationIsReplaced_PreservesReplacementAndReturnsUnavailable()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? replacementPath = null;
        var resolver = new FileSystemSemanticsResolver
        {
            AfterPrimaryProbeCreatedForTest = (primaryPath, _) =>
            {
                File.Delete(primaryPath);
                File.WriteAllText(primaryPath, "replacement");
                replacementPath = primaryPath;
            }
        };
        try
        {
            var resolution = await resolver.ResolveAsync(
                root,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.NotNull(replacementPath);
            Assert.Equal("replacement", await File.ReadAllTextAsync(replacementPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [LinuxFact]
    public async Task AutoProbe_AlternateSpellingIsOccupied_PreservesUnownedEntryAndReturnsUnavailable()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-alternate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capabilityLower = Path.Join(root, "case-capability-a");
        var capabilityUpper = Path.Join(root, "CASE-CAPABILITY-A");
        await File.WriteAllTextAsync(capabilityLower, "lower");
        try
        {
            await using var alternateCapability = new FileStream(
                capabilityUpper,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (IOException exception)
        {
            Directory.Delete(root, true);
            throw new Xunit.Sdk.XunitException(
                $"This regression requires a case-sensitive native filesystem: {exception.Message}");
        }

        File.Delete(capabilityLower);
        File.Delete(capabilityUpper);
        string? occupiedAlternate = null;
        var resolver = new FileSystemSemanticsResolver
        {
            AfterPrimaryProbeCreatedForTest = (_, alternatePath) =>
            {
                File.WriteAllText(alternatePath, "external");
                occupiedAlternate = alternatePath;
            }
        };
        try
        {
            var resolution = await resolver.ResolveAsync(
                root,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.NotNull(occupiedAlternate);
            Assert.Equal("external", await File.ReadAllTextAsync(occupiedAlternate));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [LinuxFact]
    public async Task AutoProbe_AlternateSpellingHardlinkSpoof_ReturnsUnavailableAndPreservesUnownedLink()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capabilityLower = Path.Join(root, "case-capability-a");
        var capabilityUpper = Path.Join(root, "CASE-CAPABILITY-A");
        await File.WriteAllTextAsync(capabilityLower, "lower");
        if (!TryCreateHardLink(capabilityUpper, capabilityLower))
        {
            Directory.Delete(root, true);
            Assert.Fail("The required hard link could not be created.");
        }

        File.Delete(capabilityLower);
        File.Delete(capabilityUpper);
        string? spoofedAlternate = null;
        var resolver = new FileSystemSemanticsResolver
        {
            AfterPrimaryProbeCreatedForTest = (primaryPath, alternatePath) =>
            {
                Assert.True(TryCreateHardLink(alternatePath, primaryPath));
                spoofedAlternate = alternatePath;
            }
        };
        try
        {
            var resolution = await resolver.ResolveAsync(
                root,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.NotNull(spoofedAlternate);
            Assert.True(File.Exists(spoofedAlternate));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero)
                : LinkUnix(existingPath, linkPath) == 0;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
