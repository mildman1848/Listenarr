using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Audiobooks;

[Trait("Name", "RootFolderRelocationStateTests")]
[Trait("Category", "Domain")]
public sealed class RootFolderRelocationStateTests : BaseTests
{
    [Fact]
    public void NewRoot_RequiresResolvedIdentityBeforeDestructiveWork()
    {
        var root = new RootFolder();

        Assert.Equal(FileSystemCaseSensitivityMode.Auto, root.CaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
        Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
        Assert.Null(root.PathIdentityKey);
    }

    [Fact]
    public void PersistedSemantics_ExplicitModeOverridesStaleResolvedSensitivity()
    {
        var root = new RootFolder
        {
            Path = "C:\\Library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Valid
        };

        var persisted = RootFolderPathSemantics.ResolvePersisted(root);

        Assert.NotNull(persisted);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.Value.Semantics.CaseSensitivity);
        Assert.False(persisted.Value.DetectAmbiguousCaseMatches);
    }

    [Fact]
    public void PersistedSemantics_AutoUnavailableIdentityFailsClosedDespiteStaleResolvedSensitivity()
    {
        var root = new RootFolder
        {
            Path = "C:\\Library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Unavailable
        };

        var persisted = RootFolderPathSemantics.ResolvePersisted(root);

        Assert.NotNull(persisted);
        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.Value.Semantics.CaseSensitivity);
        Assert.True(persisted.Value.DetectAmbiguousCaseMatches);
    }

    [Fact]
    public void NewRelocation_HoldsPendingPathWithoutChangingRoot()
    {
        var relocation = new RootFolderRelocation
        {
            SourcePath = "/library",
            TargetPath = "/new-library"
        };

        Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
        Assert.Equal("/new-library", relocation.TargetPath);
    }

    [Theory]
    [InlineData(
        RootFolderRelocationStatus.Pending,
        null,
        null,
        null,
        TargetIdentityEnrollmentState.LegacyUnenrolled)]
    [InlineData(
        RootFolderRelocationStatus.Running,
        1,
        "object",
        null,
        TargetIdentityEnrollmentState.Authorized)]
    [InlineData(
        RootFolderRelocationStatus.NeedsAttention,
        1,
        null,
        "unavailable",
        TargetIdentityEnrollmentState.Unavailable)]
    [InlineData(
        RootFolderRelocationStatus.Completed,
        1,
        "object",
        null,
        TargetIdentityEnrollmentState.NotRequired)]
    [InlineData(
        RootFolderRelocationStatus.Failed,
        null,
        null,
        null,
        TargetIdentityEnrollmentState.NotRequired)]
    public void TargetIdentityEnrollment_ClassificationIsDeterministic(
        RootFolderRelocationStatus status,
        int? version,
        string? identity,
        string? unavailableReason,
        TargetIdentityEnrollmentState expected)
    {
        var relocation = new RootFolderRelocation
        {
            Status = status,
            TargetDirectoryObjectIdentityVersion = version,
            TargetDirectoryObjectIdentity = identity,
            TargetDirectoryObjectIdentityUnavailableReason =
                unavailableReason
        };

        Assert.Equal(expected, TargetIdentityEnrollment.Classify(relocation));
    }
}
