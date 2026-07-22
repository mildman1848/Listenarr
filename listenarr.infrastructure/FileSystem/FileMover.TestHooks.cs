namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
}
