using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryAnchor OpenPinnedDirectoryNoFollow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
        var handle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(fullPath, openReparsePoint: true)
            : OpenDirectoryUnix(fullPath, noFollow: true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsParentIsNotReparsePoint(handle, fullPath);
            }

            var anchor = new PinnedDirectoryAnchor(
                handle,
                fullPath,
                followVisibleFinalLink: false);
            if (anchor.VisiblePathMatches())
            {
                return anchor;
            }

            anchor.Dispose();
            throw new InvalidOperationException(
                "The directory changed while it was being pinned without following links.");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedFileEntry OpenExistingFile(
            string fileName,
            bool requireDeleteAccess)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            EnsureVisiblePathMatches();
            var fullPath = Path.Join(FullPath, fileName);
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _handle,
                    fileName,
                    fullPath,
                    requireDeleteAccess)
                : OpenRelativeFileUnix(_handle, fileName, fullPath);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The file changed while it was being opened beneath its pinned parent.");
        }

        internal PinnedFileEntry? TryOpenExistingFile(
            string fileName,
            bool requireDeleteAccess)
        {
            try
            {
                return OpenExistingFile(fileName, requireDeleteAccess);
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3 or 32)
            {
                return null;
            }
        }

        internal PinnedFileEntry CreateNewFile(
            string fileName,
            bool hiddenFile = false)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, fileName, hiddenFile)
                : CreateRelativeReadWriteFileUnix(_handle, fileName);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The newly created file changed beneath its pinned parent.");
        }
    }

    internal sealed partial class PinnedFileEntry : IDisposable
    {
        private SafeFileHandle _parentHandle;
        private readonly SafeFileHandle _fileHandle;
        private string _parentPath;
        private string _fileName;
        private bool _disposed;

        internal PinnedFileEntry(
            SafeFileHandle parentHandle,
            SafeFileHandle fileHandle,
            string parentPath,
            string fileName)
        {
            _parentHandle = parentHandle;
            _fileHandle = fileHandle;
            _parentPath = parentPath;
            _fileName = fileName;
        }

        internal string FullPath => Path.Join(_parentPath, _fileName);

        internal string FileName => _fileName;

        internal SafeFileHandle DuplicateHandleForOperation()
        {
            ThrowIfDisposed();
            return DuplicateSafeHandle(_fileHandle);
        }

        internal FileStream OpenReadStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    _fileName,
                    FullPath,
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(_parentHandle, _fileName, FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Read,
                bufferSize,
                asynchronous);
        }

        internal FileStream OpenWriteStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileForWriteWindows(
                    _parentHandle,
                    _fileName,
                    FullPath)
                : OpenRelativeFileForWriteUnix(
                    _parentHandle,
                    _fileName,
                    FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Write,
                bufferSize,
                asynchronous);
        }

        internal bool VisiblePathMatches()
        {
            ThrowIfDisposed();
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenRelativeFileWindows(
                        _parentHandle,
                        _fileName,
                        FullPath,
                        requireDeleteAccess: false)
                    : OpenRelativeFileUnix(_parentHandle, _fileName, FullPath);
                return HandlesIdentifySameDirectory(_fileHandle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }

        internal void PreserveMetadataTo(PinnedFileEntry destination)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destination);
            destination.ThrowIfDisposed();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    destination._fileHandle,
                    File.GetUnixFileMode(_fileHandle));
            }
            File.SetAttributes(
                destination._fileHandle,
                File.GetAttributes(_fileHandle));
            File.SetLastWriteTimeUtc(
                destination._fileHandle,
                File.GetLastWriteTimeUtc(_fileHandle));
            File.SetCreationTimeUtc(
                destination._fileHandle,
                File.GetCreationTimeUtc(_fileHandle));
        }

        internal async Task<bool> MatchesAsync(
            long expectedLength,
            string? expectedSha256,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            await using var stream = OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            if (stream.Length != expectedLength)
            {
                return false;
            }

            stream.Position = 0;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return string.Equals(
                Convert.ToHexString(hash),
                expectedSha256,
                StringComparison.Ordinal);
        }

        internal void MoveTo(
            PinnedDirectoryAnchor destinationParent,
            string destinationName)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned rename.");
            }
            if (!destinationParent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The destination directory changed before its pinned rename.");
            }

            using var destinationHandle = destinationParent.DuplicateHandleForOperation();
            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                destinationHandle,
                destinationName);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published quarantine file does not identify the opened source file.");
            }

            var newParentHandle = DuplicateSafeHandle(destinationHandle);
            _parentHandle.Dispose();
            _parentHandle = newParentHandle;
            _parentPath = destinationParent.FullPath;
            _fileName = destinationName;
        }

        internal void MoveWithinParent(string destinationName)
        {
            ThrowIfDisposed();
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned publication.");
            }

            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                _parentHandle,
                destinationName);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published file does not identify the opened partial file.");
            }

            _fileName = destinationName;
        }

        internal void ReplaceWithinParent(
            string destinationName,
            PinnedFileEntry expectedDestination)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(expectedDestination);
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches() || !expectedDestination.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A marker changed before its atomic replacement.");
            }

            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                _parentHandle,
                destinationName,
                replaceExisting: true);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The replacement marker does not identify the flushed temporary file.");
            }

            _fileName = destinationName;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _fileHandle.Dispose();
            _parentHandle.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

    }

    private static SafeFileHandle CreateRelativeReadWriteFileUnix(
        SafeFileHandle parentHandle,
        string fileName)
    {
        var fd = OpenAt(
            parentHandle.DangerousGetHandle().ToInt32(),
            fileName,
            GetUnixReadWriteCreateFlags(),
            UnixFileMode);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not create a pinned read-write file.");
    }

    private static SafeFileHandle OpenRelativeFileUnix(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
    {
        var fd = OpenAt(
            parentHandle.DangerousGetHandle().ToInt32(),
            fileName,
            GetUnixReadFlags(),
            mode: 0);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Could not open pinned file '{fullPath}'.");
    }

    private static SafeFileHandle OpenRelativeFileForWriteUnix(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
    {
        var fd = OpenAt(
            parentHandle.DangerousGetHandle().ToInt32(),
            fileName,
            GetUnixWriteExistingFlags(),
            mode: 0);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Could not open pinned file for writing '{fullPath}'.");
    }

    private static void EnsureFileHandleIsNotReparsePoint(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileAttributeTagInformationByHandleEx(
                handle,
                FileInformationClass.FileAttributeTagInfo,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not inspect pinned file '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "A pinned file cannot be a symbolic link or reparse point.");
        }
    }

    private static int GetUnixReadFlags() => OperatingSystem.IsMacOS()
        ? 0x100 | 0x1000000
        : 0x20000 | 0x80000;

    private static int GetUnixReadWriteCreateFlags() => OperatingSystem.IsMacOS()
        ? 0x2 | 0x200 | 0x800 | 0x100 | 0x1000000
        : 0x2 | 0x40 | 0x80 | 0x20000 | 0x80000;

    private static int GetUnixWriteExistingFlags() => OperatingSystem.IsMacOS()
        ? 0x1 | 0x100 | 0x1000000
        : 0x1 | 0x20000 | 0x80000;
}
