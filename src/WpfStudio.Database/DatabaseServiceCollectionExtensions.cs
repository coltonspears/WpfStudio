using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;
using WpfStudio.Database.ViewModels;

namespace WpfStudio.Database;

public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddDatabaseFeature(this IServiceCollection services)
    {
        services.TryAddSingleton<IConnectionProfileStore, ConnectionProfileStore>();
        services.TryAddSingleton<IQueryRecoveryStore, QueryRecoveryStore>();
        services.TryAddSingleton<IDatabaseService, SqlDatabaseService>();
        services.TryAddSingleton<DatabasePaneViewModel>();
        return services;
    }
}
