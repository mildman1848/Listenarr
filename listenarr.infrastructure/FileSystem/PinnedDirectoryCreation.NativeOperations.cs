using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static SafeFileHandle OpenVisibleDirectory(string path) =>
        OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(path, openReparsePoint: true)
            : OpenDirectoryUnix(path, noFollow: true);

    private static void EnsureWindowsParentIsNotReparsePoint(
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
                $"Could not inspect directory '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "A pinned directory parent cannot be a symbolic link or reparse point.");
        }
    }

    private static SafeFileHandle OpenDirectoryWindows(
        string path,
        bool openReparsePoint)
    {
        var flags = FileFlagBackupSemantics
            | (openReparsePoint ? FileFlagOpenReparsePoint : 0u);
        var handle = CreateFileWindows(
            path,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"Could not open directory '{path}'.");
    }

    private static SafeFileHandle OpenDirectoryUnix(
        string path,
        bool noFollow)
    {
        var fd = OpenUnix(path, GetUnixDirectoryFlags(noFollow));
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Could not open directory '{path}'.");
    }

    private static SafeFileHandle OpenDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        var fd = OpenAt(
            parentHandle.DangerousGetHandle().ToInt32(),
            childName,
            GetUnixDirectoryFlags(noFollow: true),
            mode: 0);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not open a newly created pinned directory.");
    }

    private static bool HandlesIdentifySameDirectory(
        SafeFileHandle expected,
        SafeFileHandle candidate)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity(expected).Equals(GetWindowsIdentity(candidate));
        }
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxIdentity(expected).Equals(GetLinuxIdentity(candidate));
        }
        if (OperatingSystem.IsMacOS())
        {
            return string.Equals(
                GetMacHandlePath(expected),
                GetMacHandlePath(candidate),
                StringComparison.Ordinal);
        }

        throw new PlatformNotSupportedException(
            "Pinned directory identity is supported only on Windows, Linux, and macOS.");
    }

    private static WindowsFileIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInformationClass.FileIdInfo,
                out var information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static LinuxFileIdentity GetLinuxIdentity(SafeFileHandle handle)
    {
        if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                0x1000,
                0x00000100 | 0x00001000,
                out var information) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new LinuxFileIdentity(
            information.DeviceMajor,
            information.DeviceMinor,
            information.Inode,
            information.MountId);
    }

    private static string GetMacHandlePath(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            if (FcntlGetPath(
                    handle.DangerousGetHandle().ToInt32(),
                    50,
                    buffer) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUTF8(buffer)
                ?? throw new InvalidOperationException(
                    "Could not resolve a pinned macOS directory path.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CreateRelativeWindows(
        SafeFileHandle rootHandle,
        string name,
        bool directory,
        out IntPtr rawHandle)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = rootHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var desiredAccess = directory
                ? FileListDirectory | FileReadAttributes | Synchronize
                : GenericRead | GenericWrite | DeleteAccess | Synchronize;
            var createOptions = (directory ? FileDirectoryFile : FileNonDirectoryFile)
                | FileSynchronousIoNonAlert
                | FileOpenReparsePoint;
            return NtCreateFile(
                out rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                directory ? FileAttributeDirectory : FileAttributeHidden,
                FileShareAll,
                FileCreate,
                createOptions,
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

    private static Win32Exception CreateNtException(
        int status,
        string parent,
        string child)
    {
        var error = RtlNtStatusToDosError(status);
        return new Win32Exception(
            unchecked((int)error),
            $"Could not create '{child}' relative to '{parent}'.");
    }

    private static int GetUnixDirectoryFlags(bool noFollow)
    {
        var flags = OperatingSystem.IsMacOS()
            ? 0x100000 | 0x1000000
            : 0x10000 | 0x80000;
        if (noFollow)
        {
            flags |= OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        }
        return flags;
    }

    private static int GetUnixWriteFlags() => OperatingSystem.IsMacOS()
        ? 0x1 | 0x200 | 0x800 | 0x100 | 0x1000000
        : 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000;

    private static void RemoveDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                parentHandle.DangerousGetHandle().ToInt32(),
                childName,
                flags) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not remove a pinned temporary directory.");
        }
    }

    private static void TryRemoveDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        try
        {
            RemoveDirectoryAtUnix(parentHandle, childName);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UnixNoEntry)
        {
        }
        catch (Win32Exception)
        {
            // A changed temporary path is preserved rather than recursively removed.
        }
    }

    private static void ValidateLeafName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".."
            || Path.IsPathRooted(name)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException(
                "A pinned filesystem operation requires one non-navigation path segment.",
                nameof(name));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

}
