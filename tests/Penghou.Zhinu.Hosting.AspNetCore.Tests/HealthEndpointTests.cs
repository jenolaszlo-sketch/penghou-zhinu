using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Penghou.Zhinu;
using Penghou.Zhinu.Hosting;
using Penghou.Zhinu.Sqlite;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Hosting.AspNetCore.Tests;

public sealed class HealthEndpointTests
{
    private static string CreateTempDb() =>
        Path.Combine(Path.GetTempPath(), "zhinu-health", Guid.NewGuid().ToString("N"), "zhinu.db");

    private static async Task<WebApplication> StartAppAsync(string? dbPath = null, bool registerStore = true)
    {
        var builder = WebApplication.CreateBuilder();
        if (registerStore)
        {
            builder.Services.AddZhinuSqlite(options =>
            {
                options.DatabasePath = dbPath ?? CreateTempDb();
                options.Pooling = false;
            });
            builder.Services.AddZhinu();
        }
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapZhinuEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task Liveness_ReturnsOk()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var response = await client.GetAsync("/zhinu/liveness", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Readiness_WithHealthyStore_ReturnsReady()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var response = await client.GetAsync("/zhinu/readiness", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ready");
    }

    [Fact]
    public async Task Readiness_WithoutStore_ReturnsServiceUnavailable()
    {
        await using var app = await StartAppAsync(registerStore: false);
        var client = app.GetTestClient();
        var response = await client.GetAsync("/zhinu/readiness", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Diagnostics_ReportsStoreHealthWithoutPayloads()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var response = await client.GetAsync("/zhinu/diagnostics", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        doc.RootElement.GetProperty("store").GetString().Should().Be("sqlite");
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(ZhinuSqliteSchema.CurrentVersion);
        doc.RootElement.GetProperty("walMode").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StoreHealthProbe_DetectsIncompatibleSchema()
    {
        var path = CreateTempDb();
        // Initialize a store (creates the schema), then corrupt the version table.
        var first = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = path, Pooling = false });
        await first.CheckHealthAsync(TestContext.Current.CancellationToken);
        var db = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = path, Pooling = false });
        await using (var connection = await db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE zhinu_schema;";
            await command.ExecuteNonQueryAsync();
        }
        // A fresh store instance re-verifies schema compatibility on probe.
        var fresh = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = path, Pooling = false });
        var health = await fresh.CheckHealthAsync(TestContext.Current.CancellationToken);
        health.IsHealthy.Should().BeFalse();
    }
}
