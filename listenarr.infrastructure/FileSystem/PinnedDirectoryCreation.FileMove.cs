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
    }

    internal sealed class PinnedFileEntry : IDisposable
    {
        private readonly SafeFileHandle _parentHandle;
        private readonly SafeFileHandle _fileHandle;
        private readonly string _parentPath;
        private readonly string _fileName;
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

            await using var stream = new FileStream(
                DuplicateSafeHandle(_fileHandle),
                FileAccess.Read,
                bufferSize: 128 * 1024,
                isAsync: false);
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

    private static SafeFileHandle OpenRelativeFileWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath,
        bool requireDeleteAccess)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(fileName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(fileName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((fileName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var desiredAccess = GenericRead | Synchronize
                | (requireDeleteAccess ? DeleteAccess : 0u);
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareAll,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringPointer);
            }
            Marshal.FreeHGlobal(nameBuffer);
        }
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
}
