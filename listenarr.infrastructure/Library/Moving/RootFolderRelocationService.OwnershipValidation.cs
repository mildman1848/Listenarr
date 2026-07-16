using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void RejectDuplicateRelocationTargets(
        IReadOnlyCollection<AudiobookPathCandidate> affected,
        string sourceRootPath,
        string targetRootPath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        var targets = new HashSet<string>(targetSemantics.Comparer);
        foreach (var candidate in affected)
        {
            var requestedPath = MapTargetPath(
                sourceRootPath,
                targetRootPath,
                candidate.StoredBasePath,
                sourceSemantics,
                targetSemantics);
            if (!targets.Add(requestedPath))
            {
                throw new InvalidOperationException(
                    "The requested root relocation would map multiple audiobooks to the same target folder.");
            }
        }
    }

    private static void RejectDuplicateAudiobookFileOwnership(
        ListenArrDbContext db) =>
        AudiobookFileOwnershipValidator.RejectDuplicateValidOwnership(
            db.ChangeTracker.Entries<AudiobookFile>().Select(entry => entry.Entity),
            "The requested root relocation would assign the same filesystem identity to multiple audiobook files.");
}
