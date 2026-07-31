namespace Listenarr.Tests.Common;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This test requires native Windows behavior.";
        }
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
        }
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This test requires native Windows behavior.";
        }
    }
}

public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
        }
    }
}

public sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        if (NativeLinkTestsAreRequired()
            || TryCreateDirectoryLink(out _))
        {
            return;
        }

        Skip = "Directory symbolic links are unavailable on this test runner.";
    }

    private static bool NativeLinkTestsAreRequired() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "LISTENARR_REQUIRE_DIRECTORY_LINK_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateDirectoryLink(out string? reason)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-directory-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target");
        var link = Path.Join(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            reason = null;
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0
                && Directory.ResolveLinkTarget(
                    link,
                    returnFinalTarget: true) != null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            TryDeleteLinkCapabilityRoot(root, link, target);
        }
    }

    private static void TryDeleteLinkCapabilityRoot(
        string root,
        string link,
        string target)
    {
        try
        {
            if (Directory.Exists(link)
                && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(target))
            {
                Directory.Delete(target);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }
}

public sealed class FileLinkFactAttribute : FactAttribute
{
    public FileLinkFactAttribute()
    {
        if (NativeLinkTestsAreRequired()
            || TryCreateFileLink(out _))
        {
            return;
        }

        Skip = "File symbolic links are unavailable on this test runner.";
    }

    private static bool NativeLinkTestsAreRequired() =>
        string.Equals(
            Environment.GetEnvironmentVariable(
                "LISTENARR_REQUIRE_DIRECTORY_LINK_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateFileLink(out string? reason)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"listenarr-file-link-capability-{Guid.NewGuid():N}");
        var target = Path.Join(root, "target.bin");
        var link = Path.Join(root, "link.bin");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "capability");
            File.CreateSymbolicLink(link, target);
            reason = null;
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0
                && File.ResolveLinkTarget(
                    link,
                    returnFinalTarget: true) != null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(link))
                {
                    File.Delete(link);
                }
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(exception.Message);
            }
        }
    }
}
