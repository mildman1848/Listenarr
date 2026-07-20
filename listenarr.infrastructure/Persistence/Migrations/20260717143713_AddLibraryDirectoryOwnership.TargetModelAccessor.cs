using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Migrations;

partial class AddLibraryDirectoryOwnership
{
    internal static void BuildTargetModelForSuccessor(ModelBuilder modelBuilder)
    {
        new AddLibraryDirectoryOwnership().BuildTargetModel(modelBuilder);
    }
}
