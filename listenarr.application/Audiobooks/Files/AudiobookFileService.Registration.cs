namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    public async Task<bool> RegisterPublishedGenerationAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string? source = "scan",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        if (!registrationLease.MatchesCurrentPublication())
        {
            return false;
        }

        var ownership = initialOwnership;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            switch (ownership.Outcome)
            {
                case AudiobookFileOwnershipCheckOutcome.Available:
                    if (await EnsureAudiobookFileAsync(
                            audiobook,
                            registrationLease,
                            source,
                            cancellationToken))
                    {
                        if (registrationLease.MatchesCurrentPublication())
                        {
                            return true;
                        }

                        await RollbackPublishedGenerationIfStaleAsync(
                            audiobook,
                            registrationLease);
                        return false;
                    }

                    ownership = await CheckAudiobookFileOwnershipAsync(
                        audiobook,
                        registrationLease.PublicPath,
                        Path.GetDirectoryName(registrationLease.PublicPath),
                        cancellationToken);
                    continue;

                case AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook:
                    var existingFile = ownership.ExistingFile;
                    if (existingFile == null)
                    {
                        return false;
                    }

                    if (string.Equals(
                            existingFile.PhysicalObjectIdentity,
                            registrationLease.PhysicalObjectIdentity,
                            StringComparison.Ordinal))
                    {
                        if (registrationLease.MatchesCurrentPublication())
                        {
                            return true;
                        }

                        await RollbackPublishedGenerationIfStaleAsync(
                            audiobook,
                            registrationLease);
                        return false;
                    }

                    if (!await RefreshPhysicalGenerationAsync(
                            audiobook,
                            existingFile.Id,
                            existingFile.PhysicalObjectIdentity,
                            registrationLease,
                            source,
                            cancellationToken))
                    {
                        return false;
                    }

                    if (registrationLease.MatchesCurrentPublication())
                    {
                        return true;
                    }

                    await RollbackPublishedGenerationIfStaleAsync(
                        audiobook,
                        registrationLease);
                    return false;

                default:
                    return false;
            }
        }

        return false;
    }

    public async Task RollbackPublishedGenerationIfStaleAsync(
        Audiobook audiobook,
        IAudiobookFileRegistrationLease registrationLease)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        if (registrationLease.MatchesCurrentPublication())
        {
            return;
        }

        var ownership = await CheckAudiobookFileOwnershipAsync(
            audiobook,
            registrationLease.PublicPath,
            Path.GetDirectoryName(registrationLease.PublicPath),
            CancellationToken.None);
        var existingFile = ownership.Outcome
                == AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook
            ? ownership.ExistingFile
            : null;
        if (existingFile == null
            || !string.Equals(
                existingFile.PhysicalObjectIdentity,
                registrationLease.PhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!await RollbackPhysicalGenerationClaimAsync(
                audiobook,
                existingFile.Id,
                existingFile.Path,
                registrationLease.PhysicalObjectIdentity,
                CancellationToken.None))
        {
            throw new InvalidOperationException(
                "A stale imported physical-generation claim could not be rolled back.");
        }
    }
}
