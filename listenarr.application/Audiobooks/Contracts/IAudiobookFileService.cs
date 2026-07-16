
namespace Listenarr.Application.Audiobooks.Contracts
{
    /// <summary>
    /// Manages audio file metadata extraction and database tracking
    /// </summary>
    public interface IAudiobookFileService
    {
        /// <summary>
        /// Ensure an Audiobook file record exists for the given audiobook and file path. Extract metadata and persist file-level metadata.
        /// </summary>
        /// <param name="audiobook">The audiobook</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when a new ownership row was created; false when the file was already owned or could not be claimed.</returns>
        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<AudiobookFileOwnershipCheckResult> CheckAudiobookFileOwnershipAsync(
            Audiobook audiobook,
            string plannedPhysicalPath,
            string? plannedBasePath = null,
            CancellationToken cancellationToken = default);

        Task<AudiobookFileClaimResult> ClaimAudiobookFileAsync(
            Audiobook audiobook,
            AudiobookFile file,
            string physicalPath,
            CancellationToken cancellationToken = default);
    }
}
