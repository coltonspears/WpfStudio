using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WpfStudio.Core;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;

namespace WpfStudio.Database;

public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddDatabaseFeature(this IServiceCollection services)
    {
        services.AddOptions();
        services.TryAddSingleton<IConnectionProfileStore>(sp => new ConnectionProfileStore(sp.GetRequiredService<IOptions<StudioPaths>>().Value.SqlConnectionsFile));
        services.TryAddSingleton<IQueryRecoveryStore>(sp => new QueryRecoveryStore(sp.GetRequiredService<IOptions<StudioPaths>>().Value.SqlRecoveryFile));
        services.TryAddSingleton<IDatabaseService, SqlDatabaseService>();
        services.TryAddSingleton<DatabasePaneViewModel>();
        return services;
    }
}
