using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "LocalFileSystemTests")]
[Trait("Category", "Infrastructure")]
public sealed class LocalFileSystemTests : BaseTests
{
    [Fact]
    public void IsReparsePoint_DetectsLinuxSymbolicLinksOnly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Join(
            Path.GetTempPath(),
            "local-filesystem-link-" + Guid.NewGuid().ToString("N"));
        var targetDirectory = Path.Join(root, "target-directory");
        var targetFile = Path.Join(root, "target-file.m4b");
        var directoryLink = Path.Join(root, "directory-link");
        var fileLink = Path.Join(root, "file-link.m4b");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(targetFile, "audio");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, targetDirectory);
            File.CreateSymbolicLink(fileLink, targetFile);
            var fileSystem = new LocalFileSystem();

            Assert.True(fileSystem.IsReparsePoint(directoryLink));
            Assert.True(fileSystem.IsReparsePoint(fileLink));
            Assert.False(fileSystem.IsReparsePoint(targetDirectory));
            Assert.False(fileSystem.IsReparsePoint(targetFile));
        }
        finally
        {
            if (Directory.Exists(directoryLink))
            {
                Directory.Delete(directoryLink);
            }

            if (File.Exists(fileLink))
            {
                File.Delete(fileLink);
            }

            Directory.Delete(root, true);
        }
    }
}
