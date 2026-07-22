using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation : IDisposable
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
            isAsync: false);
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

}
