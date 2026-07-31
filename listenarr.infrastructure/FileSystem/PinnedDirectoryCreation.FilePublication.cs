using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedDirectoryAnchor
    {
        internal bool TryExchangeRelativeFiles(
            string firstName,
            string secondName)
        {
            ThrowIfDisposed();
            ValidateLeafName(firstName);
            ValidateLeafName(secondName);
            if (!OperatingSystem.IsLinux() || !VisiblePathMatches())
            {
                return false;
            }

            var directoryFileDescriptor = _handle
                .DangerousGetHandle()
                .ToInt32();
            return RenameAtNoReplaceLinux(
                    directoryFileDescriptor,
                    firstName,
                    directoryFileDescriptor,
                    secondName,
                    RenameExchange) == 0;
        }

        internal async Task PublishNewFileAsync(
            string temporaryFileName,
            string finalFileName,
            Func<Task> beforeCreateAsync,
            Func<FileStream, Task> writeAndFlushAsync,
            Func<Task> beforePublicationAsync,
            Func<Exception, bool> preserveTemporaryFileOnFailure)
        {
            ThrowIfDisposed();
            ValidateLeafName(temporaryFileName);
            ValidateLeafName(finalFileName);
            ArgumentNullException.ThrowIfNull(beforeCreateAsync);
            ArgumentNullException.ThrowIfNull(writeAndFlushAsync);
            ArgumentNullException.ThrowIfNull(beforePublicationAsync);
            ArgumentNullException.ThrowIfNull(preserveTemporaryFileOnFailure);

            EnsureVisiblePathMatches();
            await beforeCreateAsync();
            EnsureVisiblePathMatches();

            using var fileHandle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, temporaryFileName)
                : CreateRelativeFileUnix(_handle, temporaryFileName);
            var published = false;
            try
            {
                await using (var stream = new FileStream(
                    DuplicateSafeHandle(fileHandle),
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: false))
                {
                    await writeAndFlushAsync(stream);
                }

                await beforePublicationAsync();
                EnsureVisiblePathMatches();
                RenameRelativeEntry(
                    _handle,
                    fileHandle,
                    temporaryFileName,
                    _handle,
                    finalFileName);
                published = true;
                EnsureVisiblePathMatches();
                using var finalHandle = OperatingSystem.IsWindows()
                    ? OpenRelativeFileWindows(
                        _handle,
                        finalFileName,
                        Path.Join(FullPath, finalFileName),
                        requireDeleteAccess: false)
                    : OpenRelativeFileUnix(
                        _handle,
                        finalFileName,
                        Path.Join(FullPath, finalFileName));
                if (!HandlesIdentifySameDirectory(fileHandle, finalHandle))
                {
                    throw new InvalidOperationException(
                        "The published file does not identify the newly created pinned file.");
                }
            }
            catch (Exception exception)
            {
                if (!published && !preserveTemporaryFileOnFailure(exception))
                {
                    TryDeleteRelativeFile(_handle, fileHandle, temporaryFileName);
                }

                throw;
            }
        }
    }

    private static void RenameRelativeEntry(
        SafeFileHandle sourceDirectoryHandle,
        SafeFileHandle entryHandle,
        string sourceName,
        SafeFileHandle destinationDirectoryHandle,
        string finalName,
        bool replaceExisting = false)
    {
        if (OperatingSystem.IsWindows())
        {
            RenameRelativeEntryWindows(
                destinationDirectoryHandle,
                entryHandle,
                finalName,
                replaceExisting);
            return;
        }

        var sourceDirectoryFileDescriptor = sourceDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var destinationDirectoryFileDescriptor = destinationDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var result = replaceExisting
            ? RenameAtUnix(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName)
            : OperatingSystem.IsMacOS()
            ? RenameAtExclusiveMac(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameExclusiveMac)
            : RenameAtNoReplaceLinux(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameNoReplace);
        if (result != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not publish a pinned filesystem entry relative to its owned directory.");
        }
    }

    private static void RenameRelativeEntryWindows(
        SafeFileHandle directoryHandle,
        SafeFileHandle entryHandle,
        string finalName,
        bool replaceExisting)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(finalName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var bufferSize = checked(fileNameOffset + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            const int fileRenameInformation = 10;
            const int fileRenameInformationEx = 65;
            const int fileRenameReplaceIfExists = 0x00000001;
            const int fileRenamePosixSemantics = 0x00000002;
            if (replaceExisting)
            {
                Marshal.WriteInt32(
                    buffer,
                    0,
                    fileRenameReplaceIfExists | fileRenamePosixSemantics);
            }
            else
            {
                Marshal.WriteByte(buffer, 0, 0);
            }
            Marshal.WriteIntPtr(
                buffer,
                rootDirectoryOffset,
                directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, buffer + fileNameOffset, fileNameBytes.Length);
            var status = NtSetInformationFile(
                entryHandle,
                out _,
                buffer,
                checked((uint)bufferSize),
                replaceExisting
                    ? fileRenameInformationEx
                    : fileRenameInformation);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(
                    error,
                    $"Could not publish a pinned filesystem entry relative to its owned directory (Windows error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryDeleteRelativeFile(
        SafeFileHandle directoryHandle,
        SafeFileHandle fileHandle,
        string fileName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var deleteInformation = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(deleteInformation, 1);
                    _ = SetFileInformationByHandle(
                        fileHandle,
                        FileInformationClass.FileDispositionInfo,
                        deleteInformation,
                        sizeof(int));
                }
                finally
                {
                    Marshal.FreeHGlobal(deleteInformation);
                }

                return;
            }

            var deletionName = $".listenarr-delete-{Guid.NewGuid():N}";
            RenameRelativeEntry(
                directoryHandle,
                fileHandle,
                fileName,
                directoryHandle,
                deletionName);
            using var renamed = OpenRelativeFileUnix(
                directoryHandle,
                deletionName,
                deletionName);
            if (!HandlesIdentifySameDirectory(fileHandle, renamed))
            {
                return;
            }

            _ = UnlinkAt(
                directoryHandle.DangerousGetHandle().ToInt32(),
                deletionName,
                flags: 0);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException)
        {
            // Preserve an uncertain temporary file rather than deleting through a pathname.
        }
    }
}
