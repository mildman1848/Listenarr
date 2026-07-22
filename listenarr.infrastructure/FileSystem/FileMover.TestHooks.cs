using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Action? BeforeDirectoryMoveAttemptForTest { get; init; }
    internal Func<FileAction, string, string, Task>? BeforeFileSameContentShortcutForTestAsync { get; init; }
    internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyPublicationForTestAsync { get; init; }
}
