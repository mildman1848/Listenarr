using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.FileSystem;

internal static class ExclusiveDirectoryCreator
{
    private const int ErrorAlreadyExists = 183;
    private const int UnixAlreadyExists = 17;
    private const uint UnixDefaultMode = 0x1FF;
    private static readonly AsyncLocal<Action<string>?> BeforeCreateHook = new();

    internal static IDisposable PushBeforeCreateHook(Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = BeforeCreateHook.Value;
        BeforeCreateHook.Value = hook;
        return new HookScope(previous);
    }

    public static bool TryCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        BeforeCreateHook.Value?.Invoke(path);
        return OperatingSystem.IsWindows()
            ? TryCreateWindows(path)
            : TryCreateUnix(path);
    }

    private static bool TryCreateWindows(string path)
    {
        if (CreateDirectoryWindows(path, IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorAlreadyExists && Directory.Exists(path))
        {
            return false;
        }

        throw new Win32Exception(error, $"Could not create directory '{path}'.");
    }

    private static bool TryCreateUnix(string path)
    {
        if (CreateDirectoryUnix(path, UnixDefaultMode) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == UnixAlreadyExists && Directory.Exists(path))
        {
            return false;
        }

        throw new Win32Exception(error, $"Could not create directory '{path}'.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(
        string path,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int CreateDirectoryUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private sealed class HookScope(Action<string>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            BeforeCreateHook.Value = previous;
            _disposed = true;
        }
    }
}
