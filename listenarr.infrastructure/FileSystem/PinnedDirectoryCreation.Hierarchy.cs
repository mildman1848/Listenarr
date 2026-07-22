using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const uint FileOpen = 1;
    private const uint DuplicateSameAccess = 0x00000002;

    internal static PinnedDirectoryAnchor OpenPinnedBoundary(string boundaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);
        var handle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(boundaryPath, openReparsePoint: false)
            : OpenDirectoryUnix(boundaryPath, noFollow: false);
        var anchor = new PinnedDirectoryAnchor(
            handle,
            boundaryPath,
            followVisibleFinalLink: true);
        if (anchor.VisiblePathMatches())
        {
            return anchor;
        }

        anchor.Dispose();
        throw new InvalidOperationException(
            "The managed directory boundary changed while it was being pinned.");
    }

    internal PinnedDirectoryAnchor OpenCreatedDirectoryAnchor()
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A created pinned directory is required to continue a hierarchy walk.");
        }

        return new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            FullPath,
            followVisibleFinalLink: false);
    }

    internal sealed class PinnedDirectoryAnchor : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly bool _followVisibleFinalLink;
        private bool _disposed;

        internal PinnedDirectoryAnchor(
            SafeFileHandle handle,
            string fullPath,
            bool followVisibleFinalLink)
        {
            _handle = handle;
            FullPath = fullPath;
            _followVisibleFinalLink = followVisibleFinalLink;
        }

        internal string FullPath { get; }

        internal bool VisiblePathMatches()
        {
            ThrowIfDisposed();
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenDirectoryWindows(
                        FullPath,
                        openReparsePoint: !_followVisibleFinalLink)
                    : OpenDirectoryUnix(
                        FullPath,
                        noFollow: !_followVisibleFinalLink);
                return HandlesIdentifySameDirectory(_handle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }

        internal PinnedDirectoryAnchor OpenExistingChild(string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(childPath);
            EnsureVisiblePathMatches();

            var childHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(_handle, childName, childPath)
                : OpenDirectoryAtUnix(_handle, childName);
            var child = new PinnedDirectoryAnchor(
                childHandle,
                childPath,
                followVisibleFinalLink: false);
            if (child.VisiblePathMatches())
            {
                return child;
            }

            child.Dispose();
            throw new InvalidOperationException(
                "The visible directory hierarchy changed while opening an existing child.");
        }

        internal PinnedDirectoryCreation TryCreateChild(string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(childPath);
            EnsureVisiblePathMatches();
            return TryCreateRelative(_handle, FullPath, childName);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _handle.Dispose();
            _disposed = true;
        }

        private void EnsureVisiblePathMatches()
        {
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The visible directory hierarchy changed after its parent was pinned.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static PinnedDirectoryCreation TryCreateRelative(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName) => OperatingSystem.IsWindows()
            ? TryCreateRelativeWindows(parentHandle, parentPath, childName)
            : TryCreateRelativeUnix(parentHandle, parentPath, childName);

    private static PinnedDirectoryCreation TryCreateRelativeWindows(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName)
    {
        var ownedParentHandle = DuplicateSafeHandle(parentHandle);
        try
        {
            var status = CreateRelativeWindows(
                parentHandle,
                childName,
                directory: true,
                out var rawHandle);
            if (status == StatusObjectNameCollision)
            {
                return new PinnedDirectoryCreation(
                    ownedParentHandle,
                    directoryHandle: null,
                    parentPath,
                    childName,
                    created: false);
            }
            if (status < 0)
            {
                throw CreateNtException(status, parentPath, childName);
            }

            return new PinnedDirectoryCreation(
                ownedParentHandle,
                new SafeFileHandle(rawHandle, ownsHandle: true),
                parentPath,
                childName,
                created: true);
        }
        catch
        {
            ownedParentHandle.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation TryCreateRelativeUnix(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName)
    {
        var ownedParentHandle = DuplicateSafeHandle(parentHandle);
        var temporaryName = $".listenarr-create-{Guid.NewGuid():N}";
        SafeFileHandle? directoryHandle = null;
        var temporaryExists = false;
        try
        {
            var parentFd = parentHandle.DangerousGetHandle().ToInt32();
            if (MkdirAt(parentFd, temporaryName, UnixDirectoryMode) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not create a pinned temporary directory beneath '{parentPath}'.");
            }
            temporaryExists = true;

            directoryHandle = OpenDirectoryAtUnix(parentHandle, temporaryName);
            var renameResult = OperatingSystem.IsMacOS()
                ? RenameAtExclusiveMac(
                    parentFd,
                    temporaryName,
                    parentFd,
                    childName,
                    RenameExclusiveMac)
                : RenameAtNoReplaceLinux(
                    parentFd,
                    temporaryName,
                    parentFd,
                    childName,
                    RenameNoReplace);
            if (renameResult != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == UnixAlreadyExists)
                {
                    directoryHandle.Dispose();
                    directoryHandle = null;
                    RemoveDirectoryAtUnix(parentHandle, temporaryName);
                    temporaryExists = false;
                    return new PinnedDirectoryCreation(
                        ownedParentHandle,
                        directoryHandle: null,
                        parentPath,
                        childName,
                        created: false);
                }

                throw new Win32Exception(
                    error,
                    $"Could not publish a pinned directory beneath '{parentPath}'.");
            }

            temporaryExists = false;
            return new PinnedDirectoryCreation(
                ownedParentHandle,
                directoryHandle,
                parentPath,
                childName,
                created: true);
        }
        catch
        {
            directoryHandle?.Dispose();
            if (temporaryExists)
            {
                TryRemoveDirectoryAtUnix(parentHandle, temporaryName);
            }
            ownedParentHandle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRelativeDirectoryWindows(
        SafeFileHandle parentHandle,
        string childName,
        string childPath)
    {
        var status = OpenRelativeDirectoryWindowsCore(
            parentHandle,
            childName,
            out var rawHandle);
        if (status < 0)
        {
            throw CreateNtOpenException(status, childPath);
        }

        var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
        try
        {
            EnsureWindowsParentIsNotReparsePoint(handle, childPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static int OpenRelativeDirectoryWindowsCore(
        SafeFileHandle parentHandle,
        string childName,
        out IntPtr rawHandle)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(childName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(childName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((childName.Length + 1) * sizeof(char))),
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
            return NtCreateFile(
                out rawHandle,
                FileListDirectory | FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeDirectory,
                FileShareAll,
                FileOpen,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
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

    private static Win32Exception CreateNtOpenException(int status, string path)
    {
        var error = RtlNtStatusToDosError(status);
        return new Win32Exception(
            unchecked((int)error),
            $"Could not open pinned directory '{path}'.");
    }

    private static SafeFileHandle DuplicateSafeHandle(SafeFileHandle sourceHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var process = GetCurrentProcess();
            if (DuplicateHandleWindows(
                    process,
                    sourceHandle.DangerousGetHandle(),
                    process,
                    out var duplicate,
                    desiredAccess: 0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                return duplicate;
            }

            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not duplicate a pinned directory handle.");
        }

        var descriptor = DuplicateFileDescriptor(
            sourceHandle.DangerousGetHandle().ToInt32());
        if (descriptor >= 0)
        {
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not duplicate a pinned directory descriptor.");
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandleWindows(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateFileDescriptor(int fileDescriptor);
}
