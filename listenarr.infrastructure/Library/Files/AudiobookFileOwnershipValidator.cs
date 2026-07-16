using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Files;

internal static class AudiobookFileOwnershipValidator
{
    public static void RejectDuplicateValidOwnership(
        IEnumerable<AudiobookFile> files,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var duplicate = files
            .Where(file => file.PathIdentityState == PathIdentityState.Valid)
            .Where(file => !string.IsNullOrWhiteSpace(file.PathOwnershipKey))
            .GroupBy(file => file.PathOwnershipKey!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(file => file.Id).Distinct().Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }
}
