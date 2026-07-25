namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static async Task RetirePinnedEmptyScaffoldDirectoryAsync(
        string directoryPath,
        Action validate,
        Func<Task> authorizeMutation,
        Action beforeRetirement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(authorizeMutation);
        ArgumentNullException.ThrowIfNull(beforeRetirement);

        var fullPath = Path.GetFullPath(directoryPath);
        var parentPath = Path.GetDirectoryName(fullPath)
            ?? throw new MoveNeedsAttentionException(
                "The scaffold directory parent is unavailable.");
        var childName = Path.GetFileName(fullPath);
        using var directory = PinnedDirectoryCreation.OpenExistingForPublication(
            parentPath,
            childName);
        validate();
        await authorizeMutation();
        beforeRetirement();
        if (!directory.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The validated scaffold directory changed before retirement.");
        }

        validate();
        directory.DeletePinnedEmptyDirectory(childName);
    }
}
