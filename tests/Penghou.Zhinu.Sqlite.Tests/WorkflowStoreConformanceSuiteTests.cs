using FluentAssertions;
using Penghou.Zhinu.Testing;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowStoreConformanceSuiteTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task SqliteStore_PassesAllConformanceGroups()
    {
        var fixture = new SqliteWorkflowStoreFixture();
        var report = await WorkflowStoreConformanceSuite.VerifyAsync(fixture, TestContext.Current.CancellationToken);
        await fixture.DisposeAsync();
        report.AllPassed.Should().BeTrue(report.ToString());
    }
}
