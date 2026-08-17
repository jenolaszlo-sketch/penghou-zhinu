using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Penghou.Zhinu.Sqlite;
using System.Text.Json;

namespace Penghou.Zhinu.Agents;

/// <summary>Provides optional dependency-injection registration for the MAF integration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="SqliteJsonCheckpointStore"/> backed by the same
    /// SQLite database as the Zhinu workflow runs.
    /// </summary>
    public static IServiceCollection AddZhinuSqliteCheckpoints(
        this IServiceCollection services,
        Action<ZhinuSqliteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ZhinuSqliteOptions { DatabasePath = string.Empty };
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        services.TryAddSingleton(options);
        services.TryAddSingleton<SqliteJsonCheckpointStore>();
        services.TryAddSingleton<JsonCheckpointStore>(provider =>
            provider.GetRequiredService<SqliteJsonCheckpointStore>());
        services.TryAddSingleton<ICheckpointStore<JsonElement>>(provider =>
            provider.GetRequiredService<SqliteJsonCheckpointStore>());
        return services;
    }
}
