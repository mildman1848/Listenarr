using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Migrations;

partial class HardenMoveExecutionAndScanHandoffs
{
    internal static void BuildTargetModelForSuccessor(ModelBuilder modelBuilder)
    {
        new HardenMoveExecutionAndScanHandoffs().BuildTargetModel(modelBuilder);
    }
}
