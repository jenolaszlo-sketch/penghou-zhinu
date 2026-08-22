using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Penghou.Zhinu.Sqlite;

/// <summary>Provides optional dependency-injection registration for SQLite.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZhinuSqlite(
        this IServiceCollection services,
        Action<ZhinuSqliteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ZhinuSqliteOptions { DatabasePath = string.Empty };
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<SqliteDatabase>();
        services.TryAddSingleton<IZhinuSqliteDatabase>(provider =>
            provider.GetRequiredService<SqliteDatabase>());
        services.AddSingleton(provider => new SqliteWorkflowStore(
            provider.GetRequiredService<IZhinuSqliteDatabase>()));
        services.AddSingleton<IWorkflowStore>(provider =>
            provider.GetRequiredService<SqliteWorkflowStore>());
        return services;
    }
}
