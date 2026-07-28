namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static Task RetirePinnedEmptyScaffoldDirectoryAsync(
        string directoryPath,
        Action validate,
        Func<Task> authorizeMutation,
        Action beforeRetirement) =>
        RetirePinnedEmptyDirectoryAsync(
            directoryPath,
            "scaffold directory",
            validate,
            authorizeMutation,
            beforeRetirement);

    private static async Task RetirePinnedEmptyDirectoryAsync(
        string directoryPath,
        string description,
        Action validate,
        Func<Task> authorizeMutation,
        Action? beforeRetirement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(authorizeMutation);

        var fullPath = Path.GetFullPath(directoryPath);
        var parentPath = Path.GetDirectoryName(fullPath)
            ?? throw new MoveNeedsAttentionException(
                $"The {description} parent is unavailable.");
        var childName = Path.GetFileName(fullPath);
        using var directory = PinnedDirectoryCreation.OpenExistingForPublication(
            parentPath,
            childName);
        validate();
        await authorizeMutation();
        beforeRetirement?.Invoke();
        await authorizeMutation();
        if (!directory.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                $"The validated {description} changed before retirement.");
        }

        validate();
        directory.DeletePinnedEmptyDirectory(childName);
    }
}
