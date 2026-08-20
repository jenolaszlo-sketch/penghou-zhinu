using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Penghou.Zhinu.OpenTelemetry;

namespace Penghou.Zhinu.OpenTelemetry.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task AddZhinuInstrumentation_RegistersProvidersWithoutExporter()
    {
        var services = new ServiceCollection();
        services.AddOpenTelemetry().AddZhinuInstrumentation();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().NotBeNull();
        provider.GetService<MeterProvider>().Should().NotBeNull();
    }
}
