using System.Buffers;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class PinnedAudiobookFileRegistrationLease :
    IAudiobookFileRegistrationLease
{
    private readonly PinnedDirectoryCreation.PinnedFileEntry _file;
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _stableHandle;
    private readonly Func<bool>? _completePublication;
    private bool _publicationCompleted;
    private bool _disposed;

    private PinnedAudiobookFileRegistrationLease(
        PinnedDirectoryCreation.PinnedFileEntry file,
        Microsoft.Win32.SafeHandles.SafeFileHandle? stableHandle,
        string publicPath,
        string metadataPath,
        string physicalObjectIdentity,
        string? sourcePhysicalObjectIdentity,
        Func<bool>? completePublication)
    {
        _file = file;
        _stableHandle = stableHandle;
        _completePublication = completePublication;
        PublicPath = publicPath;
        MetadataPath = metadataPath;
        PhysicalObjectIdentity = physicalObjectIdentity;
        SourcePhysicalObjectIdentity = sourcePhysicalObjectIdentity;
    }

    public string PublicPath { get; }
    public string MetadataPath { get; }
    public string PhysicalObjectIdentity { get; }
    public string? SourcePhysicalObjectIdentity { get; }

    internal static PinnedAudiobookFileRegistrationLease Open(
        string publicPath,
        string? expectedPhysicalObjectIdentity = null,
        string? sourcePhysicalObjectIdentity = null,
        Func<bool>? completePublication = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        var canonicalPath = Path.GetFullPath(publicPath);
        var parentPath = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The audiobook file path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            parentPath,
            createMissing: false);
        var file = parent.OpenExistingFileForStableRead(
            Path.GetFileName(canonicalPath));
        return Create(
            file,
            canonicalPath,
            expectedPhysicalObjectIdentity,
            sourcePhysicalObjectIdentity,
            completePublication);
    }

    internal static PinnedAudiobookFileRegistrationLease Create(
        PinnedDirectoryCreation.PinnedFileEntry file,
        string publicPath,
        string? expectedPhysicalObjectIdentity = null,
        string? sourcePhysicalObjectIdentity = null,
        Func<bool>? completePublication = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        Microsoft.Win32.SafeHandles.SafeFileHandle? stableHandle = null;
        try
        {
            var canonicalPath = Path.GetFullPath(publicPath);
            var physicalObjectIdentity = file.GetObjectIdentity();
            if (!file.VisiblePathMatches()
                || (!string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity)
                    && !string.Equals(
                        physicalObjectIdentity,
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The audiobook file generation does not match the expected physical identity.");
            }

            if (OperatingSystem.IsWindows())
            {
                return new PinnedAudiobookFileRegistrationLease(
                    file,
                    null,
                    canonicalPath,
                    canonicalPath,
                    physicalObjectIdentity,
                    sourcePhysicalObjectIdentity,
                    completePublication);
            }

            if (OperatingSystem.IsLinux())
            {
                stableHandle = file.DuplicateHandleForOperation();
                var metadataPath = FormattableString.Invariant(
                    $"/proc/{Environment.ProcessId}/fd/{stableHandle.DangerousGetHandle().ToInt32()}");
                if (!File.Exists(metadataPath))
                {
                    throw new PlatformNotSupportedException(
                        "The Linux proc filesystem is unavailable for stable metadata extraction.");
                }

                var result = new PinnedAudiobookFileRegistrationLease(
                    file,
                    stableHandle,
                    canonicalPath,
                    metadataPath,
                    physicalObjectIdentity,
                    sourcePhysicalObjectIdentity,
                    completePublication);
                stableHandle = null;
                return result;
            }

            throw new PlatformNotSupportedException(
                "Stable metadata extraction is supported only on Windows and Linux.");
        }
        catch
        {
            stableHandle?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public async Task<bool> MatchesContentAsync(
        Stream candidateStream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidateStream);

        await using var publishedStream = _file.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        if (candidateStream.CanSeek)
        {
            if (candidateStream.Length != publishedStream.Length)
            {
                return false;
            }

            candidateStream.Position = 0;
        }
        publishedStream.Position = 0;

        var candidateBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var publishedBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                var candidateRead = await candidateStream.ReadAsync(
                    candidateBuffer.AsMemory(0, candidateBuffer.Length),
                    cancellationToken);
                var publishedRead = await publishedStream.ReadAsync(
                    publishedBuffer.AsMemory(0, publishedBuffer.Length),
                    cancellationToken);
                if (candidateRead != publishedRead)
                {
                    return false;
                }

                if (candidateRead == 0)
                {
                    return true;
                }

                if (!candidateBuffer.AsSpan(0, candidateRead).SequenceEqual(
                        publishedBuffer.AsSpan(0, publishedRead)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(candidateBuffer);
            ArrayPool<byte>.Shared.Return(publishedBuffer);
        }
    }

    public bool MatchesCurrentPublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return _file.VisiblePathMatches()
                && string.Equals(
                    _file.GetObjectIdentity(),
                    PhysicalObjectIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool CompletePublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publicationCompleted)
        {
            return true;
        }

        if (_completePublication != null && !_completePublication())
        {
            return false;
        }

        _publicationCompleted = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stableHandle?.Dispose();
        _file.Dispose();
        _disposed = true;
    }
}
