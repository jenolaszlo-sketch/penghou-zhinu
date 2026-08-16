using Microsoft.Extensions.DependencyInjection;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        services.AddSingleton(options);
        services.AddSingleton<SqliteWorkflowStore>();
        services.AddSingleton<IWorkflowStore>(provider =>
            provider.GetRequiredService<SqliteWorkflowStore>());
        return services;
    }
}
