using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Action? BeforeDirectoryMoveAttemptForTest { get; init; }
    internal Func<FileAction, string, string, Task>? BeforeFileSameContentShortcutForTestAsync { get; init; }
    internal Func<FileAction, string, string, Task>? AfterFileEndpointsPinnedForTestAsync { get; init; }
    internal Func<FileAction, string, string, Task>? AfterFileEntriesPinnedForTestAsync { get; init; }
    internal Func<FileAction, string, string, Task>? AfterPinnedSourceContentCapturedForTestAsync { get; init; }
    internal Func<Task>? BeforePinnedHardlinkCreationForTestAsync { get; init; }
    internal bool DisableNativeFileRenameForTest { get; init; }
    internal Action<string>? BeforeFileMoveDurabilityBarrierForTest { get; init; }
    internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyPublicationForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyStagingCleanupForTestAsync { get; init; }
    internal string? FileMoveLockDirectoryForTest { get; init; }
}
