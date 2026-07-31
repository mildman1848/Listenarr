namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static string GetConditionalReplacementBackupName(
        string destinationName)
    {
        ValidateLeafName(destinationName);
        return destinationName + ".listenarr-predecessor.tmp";
    }

    internal sealed partial class PinnedFileEntry
    {
        internal void ReplaceWithinParent(
            string destinationName,
            PinnedFileEntry expectedDestination,
            Action? beforeReplacement = null,
            Action? afterPublication = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(expectedDestination);
            ValidateLeafName(destinationName);
            if (!HandlesIdentifySameDirectory(
                    _parentHandle,
                    expectedDestination._parentHandle))
            {
                throw new InvalidOperationException(
                    "A pinned replacement requires both files to share the same parent directory.");
            }
            if (!VisiblePathMatches() || !expectedDestination.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A marker changed before its conditional replacement.");
            }

            beforeReplacement?.Invoke();

            if (OperatingSystem.IsWindows())
            {
                ReplaceWithinParentWindows(
                    destinationName,
                    expectedDestination,
                    afterPublication);
                return;
            }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                ReplaceWithinParentUnix(
                    destinationName,
                    expectedDestination,
                    afterPublication);
                return;
            }

            throw new PlatformNotSupportedException(
                "Conditional pinned-file replacement is not available on this platform.");
        }

        private void ReplaceWithinParentWindows(
            string destinationName,
            PinnedFileEntry expectedDestination,
            Action? afterPublication)
        {
            using var stableExpected = new PinnedFileEntry(
                DuplicateSafeHandle(_parentHandle),
                OpenRelativeFileStableDeleteWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName)),
                _parentPath,
                destinationName,
                _parentFollowsVisibleFinalLink);
            if (!expectedDestination.IdentifiesSameEntry(stableExpected))
            {
                throw new InvalidOperationException(
                    "The marker destination changed before its predecessor could be pinned.");
            }

            var backupName =
                GetConditionalReplacementBackupName(destinationName);
            stableExpected.MoveWithinParent(backupName);
            FlushDirectoryPathToDisk(_parentHandle, _parentPath);
            try
            {
                MoveWithinParent(destinationName);
                FlushDirectoryPathToDisk(_parentHandle, _parentPath);
                var publishedIdentity = GetObjectIdentity();
                _fileHandle.Dispose();
                _fileHandle = OpenRelativeFileStableReadWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName));
                if (!string.Equals(
                        GetObjectIdentity(),
                        publishedIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The published marker changed before it could be pinned.");
                }

                afterPublication?.Invoke();
                if (!VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The published marker changed before predecessor retirement.");
                }

                stableExpected.Delete(immediateWindows: true);
            }
            catch
            {
                TryRestoreExpectedDestination(stableExpected, destinationName);
                throw;
            }

            FlushDirectoryPathToDisk(_parentHandle, _parentPath);
        }

        private void ReplaceWithinParentUnix(
            string destinationName,
            PinnedFileEntry expectedDestination,
            Action? afterPublication)
        {
            if (!VisiblePathMatches() || !expectedDestination.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A marker changed before its conditional exchange.");
            }

            var parentFileDescriptor = _parentHandle
                .DangerousGetHandle()
                .ToInt32();
            if (ExchangeRelativeFilesUnix(
                    parentFileDescriptor,
                    _fileName,
                    destinationName) != 0)
            {
                throw new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "Could not atomically exchange a pinned replacement marker with its expected predecessor.");
            }

            FlushDirectoryPathToDisk(_parentHandle, _parentPath);
            var originalTemporaryName = _fileName;
            using var displaced = OpenPinnedSibling(
                originalTemporaryName,
                requireDeleteAccess: true);
            if (!expectedDestination.IdentifiesSameEntry(displaced))
            {
                if (ExchangeRelativeFilesUnix(
                        parentFileDescriptor,
                        originalTemporaryName,
                        destinationName) != 0)
                {
                    throw new InvalidOperationException(
                        "The marker exchange displaced an unexpected generation and could not be rolled back.");
                }

                FlushDirectoryPathToDisk(_parentHandle, _parentPath);
                throw new InvalidOperationException(
                    "The marker destination changed during its conditional exchange.");
            }

            _fileName = destinationName;
            afterPublication?.Invoke();
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The published marker changed before predecessor retirement.");
            }

            displaced.Delete(immediateWindows: true);
            FlushDirectoryPathToDisk(_parentHandle, _parentPath);
        }

        private static int ExchangeRelativeFilesUnix(
            int parentFileDescriptor,
            string firstName,
            string secondName)
        {
            return OperatingSystem.IsMacOS()
                ? RenameAtExclusiveMac(
                    parentFileDescriptor,
                    firstName,
                    parentFileDescriptor,
                    secondName,
                    RenameSwapMac)
                : RenameAtNoReplaceLinux(
                    parentFileDescriptor,
                    firstName,
                    parentFileDescriptor,
                    secondName,
                    RenameExchange);
        }

        private PinnedFileEntry OpenPinnedSibling(
            string fileName,
            bool requireDeleteAccess)
        {
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    fileName,
                    Path.Join(_parentPath, fileName),
                    requireDeleteAccess)
                : OpenRelativeFileUnix(
                    _parentHandle,
                    fileName,
                    Path.Join(_parentPath, fileName));
            return new PinnedFileEntry(
                DuplicateSafeHandle(_parentHandle),
                handle,
                _parentPath,
                fileName,
                _parentFollowsVisibleFinalLink);
        }

        private static void TryRestoreExpectedDestination(
            PinnedFileEntry stableExpected,
            string destinationName)
        {
            try
            {
                stableExpected.MoveWithinParent(destinationName);
                FlushDirectoryPathToDisk(
                    stableExpected._parentHandle,
                    stableExpected._parentPath);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception
                    or InvalidOperationException)
            {
                // Preserve the exact predecessor under its backup name. The caller
                // receives the original publication failure and can retry recovery
                // without deleting an unrelated destination generation.
            }
        }
    }
}
