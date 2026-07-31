using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed class RegistrationPublicationCleanupService(
    IRegistrationPublicationCleanupProcessor processor,
    IWorkerCycleRunner cycleRunner,
    ILogger<RegistrationPublicationCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RegistrationPublicationCleanupService starting");
        await cycleRunner.RunPeriodicAsync(
            nameof(RegistrationPublicationCleanupService),
            initialDelay: null,
            intervalProvider: static () => Interval,
            runCycle: processor.RunCycleAsync,
            stoppingToken);
        logger.LogInformation("RegistrationPublicationCleanupService stopping");
    }
}
