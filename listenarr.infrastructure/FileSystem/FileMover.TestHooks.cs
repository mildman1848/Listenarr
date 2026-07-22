namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyPublicationForTestAsync { get; init; }
    internal Func<string, Task>? BeforeEmptyDestinationPlaceholderQuarantineForTestAsync { get; init; }
}
