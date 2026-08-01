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
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class DirectoryLinkTheoryAttribute : TheoryAttribute
{
    public DirectoryLinkTheoryAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class FileLinkFactAttribute : FactAttribute
{
    public FileLinkFactAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class FileLinkTheoryAttribute : TheoryAttribute
{
    public FileLinkTheoryAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class LinuxDirectoryAndFileLinkFactAttribute : FactAttribute
{
    public LinuxDirectoryAndFileLinkFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
            return;
        }

        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks,
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}
