using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Penghou.Zhinu.Hosting;

/// <summary>
/// Maps minimal health and diagnostics endpoints for a Zhinu runtime.
/// Liveness answers "is the process alive enough to answer"; readiness answers
/// "can this host participate in workflow execution" via
/// <see cref="IWorkflowStore.CheckHealthAsync"/>; diagnostics exposes runtime
/// health without any workflow payloads, metadata, or artifact contents.
/// </summary>
public static class WorkflowEngineEndpoints
{
    /// <summary>Maps <c>/liveness</c>, <c>/readiness</c>, and <c>/diagnostics</c> under <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapZhinuEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/zhinu")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        endpoints.MapGet($"{prefix}/liveness", () => Results.Text("ok"));

        endpoints.MapGet($"{prefix}/readiness", async (HttpContext http, CancellationToken ct) =>
        {
            var store = http.RequestServices.GetService<IWorkflowStore>();
            if (store is null)
                return Results.Json(new { status = "not_ready", detail = "No IWorkflowStore registered." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var health = await store.CheckHealthAsync(ct);
            return health.IsHealthy
                ? Results.Json(new { status = "ready" })
                : Results.Json(new { status = "not_ready", detail = health.Detail }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        endpoints.MapGet($"{prefix}/diagnostics", async (HttpContext http, CancellationToken ct) =>
        {
            var store = http.RequestServices.GetService<IWorkflowStore>();
            var health = store is null
                ? new WorkflowStoreHealth { IsHealthy = false, Detail = "No IWorkflowStore registered." }
                : await store.CheckHealthAsync(ct);
            return Results.Json(new
            {
                status = health.IsHealthy ? "healthy" : "degraded",
                store = health.StoreName,
                schemaVersion = health.SchemaVersion,
                walMode = health.WalMode,
                detail = health.Detail
            });
        });

        return endpoints;
    }
}
