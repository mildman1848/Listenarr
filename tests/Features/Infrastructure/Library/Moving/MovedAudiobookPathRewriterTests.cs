using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MovedAudiobookPathRewriterTests")]
[Trait("Category", "Infrastructure")]
public sealed class MovedAudiobookPathRewriterTests : BaseTests
{
    [Fact]
    public async Task RewriteAsync_UnmappableStoredPath_RequiresOperatorAttention()
    {
        var repository = new Mock<IAudiobookRepository>();
        repository.Setup(candidate => candidate.RewritePathReferencesAsync(
                42,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AudiobookPathRewriteException(
                "Stored audiobook path could not be mapped to the new base path."));
        var logger = Mock.Of<ILogger>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            MovedAudiobookPathRewriter.RewriteAsync(
                42,
                Path.GetFullPath("source"),
                Path.GetFullPath("target"),
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault,
                repository.Object,
                logger,
                CancellationToken.None));

        Assert.Contains("could not be mapped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RewriteAsync_OwnershipConstraintConflict_RequiresOperatorAttention()
    {
        var repository = new Mock<IAudiobookRepository>();
        repository.Setup(candidate => candidate.RewritePathReferencesAsync(
                42,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UniqueConstraintViolationException(
                "Ownership conflict.",
                new InvalidOperationException("UNIQUE constraint failed.")));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            MovedAudiobookPathRewriter.RewriteAsync(
                42,
                Path.GetFullPath("source"),
                Path.GetFullPath("target"),
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault,
                repository.Object,
                Mock.Of<ILogger>(),
                CancellationToken.None));

        Assert.Contains("ownership", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
