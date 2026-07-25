namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static async Task RetirePinnedArtifactAsync(
        string artifactPath,
        Action<PinnedDirectoryCreation.PinnedFileEntry> validate,
        Func<Task> authorizeMutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(authorizeMutation);

        var fullPath = Path.GetFullPath(artifactPath);
        var parentPath = Path.GetDirectoryName(fullPath)
            ?? throw new MoveNeedsAttentionException(
                "The artifact parent directory is unavailable.");
        using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            parentPath);
        using var entry = parent.OpenExistingFile(
            Path.GetFileName(fullPath),
            requireDeleteAccess: true);
        validate(entry);
        await authorizeMutation();
        if (!parent.VisiblePathMatches() || !entry.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The validated artifact changed before retirement.");
        }

        validate(entry);
        entry.Delete();
    }
}
