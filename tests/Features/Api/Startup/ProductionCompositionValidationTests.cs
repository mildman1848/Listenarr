using Listenarr.Api.Startup;
using Listenarr.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Listenarr.Tests.Features.Api.Startup;

[Trait("Name", "ProductionCompositionValidationTests")]
[Trait("Category", "Api")]
public sealed class ProductionCompositionValidationTests
{
    [Fact]
    public void DevelopmentComposition_ValidatesCompleteProductionServiceGraph()
    {
        var contentRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"development-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                EnvironmentName = Environments.Development
            });
            var fileSystem = new LocalFileSystem();
            builder.Configuration["Listenarr:SqliteDbPath"] = Path.Join(
                contentRoot,
                "composition.db");
            builder.AddListenarrApiServices(fileSystem);
            builder.Services.AddListenarrInfrastructureComposition(
                builder.Configuration,
                builder.Environment);

            Assert.Contains(builder.Services, descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(MoveBackgroundService));

            using var provider = builder.Services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });

            Assert.NotNull(provider.GetRequiredService<IRootFolderRelocationService>());
            Assert.NotNull(provider.GetRequiredService<IMoveQueueService>());
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
