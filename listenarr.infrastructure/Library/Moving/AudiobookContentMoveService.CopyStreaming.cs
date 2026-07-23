using System.Buffers;
using System.Diagnostics;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const int MoveCopyBufferSize = 1024 * 1024;
    private const long MoveCopyLeaseCheckBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan MoveCopyLeaseCheckInterval = TimeSpan.FromSeconds(5);

    private async Task CopyFileWithLeaseChecksAsync(
        AudiobookContentMoveRequest request,
        string sourceRoot,
        string target,
        string sourceFile,
        string destinationFile,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MoveCopyBufferSize);
        try
        {
            await using var sourceStream = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MoveCopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            faultInjector?.OnCopyMutation(
                request.JobId,
                CopyMutationFaultPoint.BeforePartialFileCreation);
            await using var destinationStream = new FileStream(
                destinationFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                MoveCopyBufferSize,
                FileOptions.Asynchronous);

            var bytesSinceLeaseCheck = 0L;
            var leaseCheckTimer = Stopwatch.StartNew();
            var firstChunk = true;
            while (true)
            {
                var bytesRead = await sourceStream.ReadAsync(
                    buffer.AsMemory(0, MoveCopyBufferSize),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await destinationStream.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                bytesSinceLeaseCheck += bytesRead;

                if (firstChunk && faultInjector != null)
                {
                    faultInjector.OnCopyMutation(
                        request.JobId,
                        CopyMutationFaultPoint.AfterChunkWritten);
                    await EnsureMutationAuthorizedAsync(
                        request,
                        sourceRoot,
                        target,
                        cancellationToken);
                    firstChunk = false;
                    bytesSinceLeaseCheck = 0;
                    leaseCheckTimer.Restart();
                }
                else if (bytesSinceLeaseCheck >= MoveCopyLeaseCheckBytes
                    || leaseCheckTimer.Elapsed >= MoveCopyLeaseCheckInterval)
                {
                    await EnsureMutationAuthorizedAsync(
                        request,
                        sourceRoot,
                        target,
                        cancellationToken);
                    bytesSinceLeaseCheck = 0;
                    leaseCheckTimer.Restart();
                }
            }

            await EnsureMutationAuthorizedAsync(
                request,
                sourceRoot,
                target,
                cancellationToken);
            destinationStream.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
