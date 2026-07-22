using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class PinnedDirectoryCreation : IDisposable
{
    private const int UnixAlreadyExists = 17;
    private const int UnixNoEntry = 2;
    private const uint UnixDirectoryMode = 0x1FF;
    private const uint UnixFileMode = 0x180;
    private const int AtRemovedirLinux = 0x200;
    private const int AtRemovedirMac = 0x80;
    private const uint RenameNoReplace = 1;
    private const uint RenameExclusiveMac = 4;

    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    private readonly SafeFileHandle _parentHandle;
    private readonly SafeFileHandle? _directoryHandle;
    private readonly string _parentPath;
    private readonly string _childName;
    private bool _disposed;

    private PinnedDirectoryCreation(
        SafeFileHandle parentHandle,
        SafeFileHandle? directoryHandle,
        string parentPath,
        string childName,
        bool created)
    {
        _parentHandle = parentHandle;
        _directoryHandle = directoryHandle;
        _parentPath = parentPath;
        _childName = childName;
        Created = created;
    }

    public bool Created { get; }

    public string FullPath => Path.Join(_parentPath, _childName);

    public static PinnedDirectoryCreation TryCreate(string parentPath, string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateLeafName(childName);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(parentPath);

        return OperatingSystem.IsWindows()
            ? TryCreateWindows(parentPath, childName)
            : TryCreateUnix(parentPath, childName);
    }

    public bool VisiblePathMatches()
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            return false;
        }

        try
        {
            using var visible = OpenVisibleDirectory(FullPath);
            return HandlesIdentifySameDirectory(_directoryHandle, visible);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public Task WriteInsideFileAsync(
        string fileName,
        string contents,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required to write an inside marker.");
        }

        return WriteNewRelativeFileAsync(
            _directoryHandle,
            fileName,
            contents,
            cancellationToken);
    }

    public Task WriteParentFileAsync(
        string fileName,
        string contents,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!Created)
        {
            throw new InvalidOperationException(
                "A newly created pinned directory is required to write a sibling marker.");
        }

        return WriteNewRelativeFileAsync(
            _parentHandle,
            fileName,
            contents,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _directoryHandle?.Dispose();
        _parentHandle.Dispose();
        _disposed = true;
    }

    private static PinnedDirectoryCreation TryCreateWindows(
        string parentPath,
        string childName)
    {
        var parentHandle = OpenDirectoryWindows(parentPath, openReparsePoint: true);
        try
        {
            EnsureWindowsParentIsNotReparsePoint(parentHandle, parentPath);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(Path.Join(parentPath, childName));
            var status = CreateRelativeWindows(
                parentHandle,
                childName,
                directory: true,
                out var rawHandle);
            if (status == StatusObjectNameCollision)
            {
                return new PinnedDirectoryCreation(
                    parentHandle,
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
                parentHandle,
                new SafeFileHandle(rawHandle, ownsHandle: true),
                parentPath,
                childName,
                created: true);
        }
        catch
        {
            parentHandle.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation TryCreateUnix(
        string parentPath,
        string childName)
    {
        var parentHandle = OpenDirectoryUnix(parentPath, noFollow: true);
        var temporaryName = $".listenarr-create-{Guid.NewGuid():N}";
        SafeFileHandle? directoryHandle = null;
        var temporaryExists = false;
        try
        {
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(Path.Join(parentPath, childName));
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
                ? RenameAtExclusiveMac(parentFd, temporaryName, parentFd, childName, RenameExclusiveMac)
                : RenameAtNoReplaceLinux(parentFd, temporaryName, parentFd, childName, RenameNoReplace);
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
                        parentHandle,
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
                parentHandle,
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
            parentHandle.Dispose();
            throw;
        }
    }

    private static async Task WriteNewRelativeFileAsync(
        SafeFileHandle directoryHandle,
        string fileName,
        string contents,
        CancellationToken cancellationToken)
    {
        ValidateLeafName(fileName);
        using var fileHandle = OperatingSystem.IsWindows()
            ? CreateRelativeFileWindows(directoryHandle, fileName)
            : CreateRelativeFileUnix(directoryHandle, fileName);
        await using var stream = new FileStream(
            fileHandle,
            FileAccess.Write,
            bufferSize: 4096,
            isAsync: true);
        var bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static SafeFileHandle CreateRelativeFileWindows(
        SafeFileHandle directoryHandle,
        string fileName)
    {
        var status = CreateRelativeWindows(
            directoryHandle,
            fileName,
            directory: false,
            out var rawHandle);
        if (status == StatusObjectNameCollision)
        {
            throw new InvalidOperationException(
                "A durable ownership marker unexpectedly already exists.");
        }
        if (status < 0)
        {
            throw CreateNtException(status, "pinned directory", fileName);
        }

        return new SafeFileHandle(rawHandle, ownsHandle: true);
    }

    private static SafeFileHandle CreateRelativeFileUnix(
        SafeFileHandle directoryHandle,
        string fileName)
    {
        var flags = GetUnixWriteFlags();
        var fd = OpenAt(
            directoryHandle.DangerousGetHandle().ToInt32(),
            fileName,
            flags,
            UnixFileMode);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        var error = Marshal.GetLastWin32Error();
        if (error == UnixAlreadyExists)
        {
            throw new InvalidOperationException(
                "A durable ownership marker unexpectedly already exists.");
        }

        throw new Win32Exception(error, "Could not create a pinned ownership marker.");
    }

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
                : GenericRead | GenericWrite | Synchronize;
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

    private readonly record struct WindowsFileIdentity(
        ulong VolumeSerialNumber,
        ulong LowPart,
        ulong HighPart);

    private readonly record struct LinuxFileIdentity(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        ulong MountId);

    private enum FileInformationClass
    {
        FileAttributeTagInfo = 9,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxInformation
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModificationTime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Spare00;
        public ulong Spare01;
        public ulong Spare02;
        public ulong Spare03;
        public ulong Spare04;
        public ulong Spare05;
        public ulong Spare06;
        public ulong Spare07;
        public ulong Spare08;
        public ulong Spare09;
        public ulong Spare10;
        public ulong Spare11;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInformationClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInformationClass fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAtNoReplaceLinux(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtExclusiveMac(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out StatxInformation information);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlGetPath(
        int fileDescriptor,
        int command,
        IntPtr buffer);
}
