using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// `runs list --created-after/--created-before` over directly seeded runs with
/// known creation times: inclusive boundaries, UTC normalization of offsets,
/// composition with other filters, and usage rejection of invalid or inverted
/// bounds. The CLI stays a thin projection over <see cref="Penghou.Zhinu.RunQuery"/>.
/// </summary>
public sealed class RunListDateFilterCliTests : WorkflowEngineTestBase
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Twelve = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fourteen = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_DateBoundsAreInclusiveAndCompose()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = await CreateRunAsync(store, "alpha", Ten, null, ct);
        var b = await CreateRunAsync(store, "beta", Twelve, null, ct);
        var c = await CreateRunAsync(store, "alpha", Fourteen, null, ct);

        // created_at >= bound: a run exactly on the bound is included.
        var after = await RunCliAsync("runs", "list", "--created-after", "2026-10-01T12:00:00Z");
        after.Code.Should().Be(0);
        after.Output.Should().Contain(b.ToString("D")).And.Contain(c.ToString("D"))
            .And.NotContain(a.ToString("D"));

        // created_at <= bound: a run exactly on the bound is included.
        var before = await RunCliAsync("runs", "list", "--created-before", "2026-10-01T12:00:00Z");
        before.Code.Should().Be(0);
        before.Output.Should().Contain(a.ToString("D")).And.Contain(b.ToString("D"))
            .And.NotContain(c.ToString("D"));

        // Both bounds plus a workflow name filter compose.
        var combined = await RunCliAsync(
            "runs", "list",
            "--created-after", "2026-10-01T10:00:00Z",
            "--created-before", "2026-10-01T14:00:00Z",
            "--workflow", "alpha");
        combined.Code.Should().Be(0);
        combined.Output.Should().Contain(a.ToString("D")).And.Contain(c.ToString("D"))
            .And.NotContain(b.ToString("D"));

        // Version filter follows the same path. The shared seed helper fixes
        // version "1", so seed one version-2 run directly.
        var d = Guid.NewGuid();
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = d,
            WorkflowName = "delta",
            WorkflowVersion = "2",
            Status = WorkflowStatus.Pending,
            CreatedAt = Twelve,
            UpdatedAt = Twelve
        }, ct);
        var version = await RunCliAsync(
            "runs", "list",
            "--created-after", "2026-10-01T10:00:00Z",
            "--created-before", "2026-10-01T14:00:00Z",
            "--version", "2");
        version.Code.Should().Be(0);
        version.Output.Should().Contain(d.ToString("D")).And.NotContain(a.ToString("D"))
            .And.NotContain(b.ToString("D"));
    }

    [Fact]
    public async Task List_OffsetBoundsNormalizeToUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = await CreateRunAsync(store, "alpha", Ten, null, ct);
        var b = await CreateRunAsync(store, "beta", Twelve, null, ct);
        var c = await CreateRunAsync(store, "alpha", Fourteen, null, ct);

        // 13:00+02:00 is 11:00Z: after a (10:00Z), at or before b (12:00Z) and c (14:00Z).
        var after = await RunCliAsync("runs", "list", "--created-after", "2026-10-01T13:00:00+02:00");
        after.Code.Should().Be(0);
        after.Output.Should().Contain(b.ToString("D")).And.Contain(c.ToString("D"))
            .And.NotContain(a.ToString("D"));

        var before = await RunCliAsync("runs", "list", "--created-before", "2026-10-01T13:00:00+02:00");
        before.Code.Should().Be(0);
        before.Output.Should().Contain(a.ToString("D")).And.NotContain(b.ToString("D"));
    }

    [Fact]
    public async Task List_AppliesEachBoundIndependently()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = await CreateRunAsync(store, "alpha", Ten, null, ct);
        var b = await CreateRunAsync(store, "beta", Twelve, null, ct);

        var afterOnly = await RunCliAsync("runs", "list", "--created-after", "2026-10-01T11:00:00Z");
        afterOnly.Output.Should().Contain(b.ToString("D")).And.NotContain(a.ToString("D"));

        var beforeOnly = await RunCliAsync("runs", "list", "--created-before", "2026-10-01T11:00:00Z");
        beforeOnly.Output.Should().Contain(a.ToString("D")).And.NotContain(b.ToString("D"));
    }

    [Fact]
    public async Task List_RejectsInvalidAndInvertedBounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await CreateRunAsync(store, "alpha", Ten, null, ct);

        var invalid = await RunCliAsync("runs", "list", "--created-after", "yesterday");
        invalid.Code.Should().Be(1);
        invalid.Output.Should().Contain("ISO 8601");

        var inverted = await RunCliAsync(
            "runs", "list",
            "--created-after", "2026-10-01T14:00:00Z",
            "--created-before", "2026-10-01T10:00:00Z");
        inverted.Code.Should().Be(1);
        inverted.Output.Should().Contain("must not be later");
    }

    [Fact]
    public async Task List_BoundsComposeWithPagination()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = await CreateRunAsync(store, "alpha", Ten, null, ct);
        var b = await CreateRunAsync(store, "beta", Twelve, null, ct);
        await CreateRunAsync(store, "gamma", Fourteen, null, ct);

        var firstPage = await RunCliAsync("runs", "list", "--created-after", "2026-10-01T10:00:00Z", "--limit", "1");
        firstPage.Code.Should().Be(0);
        firstPage.Output.Should().Contain(a.ToString("D")).And.NotContain(b.ToString("D"));

        var nextPage = await RunCliAsync(
            "runs", "list", "--created-after", "2026-10-01T10:00:00Z", "--after", a.ToString("D"), "--limit", "1");
        nextPage.Code.Should().Be(0);
        nextPage.Output.Should().Contain(b.ToString("D")).And.NotContain(a.ToString("D"));
    }

    private async Task<(int Code, string Output)> RunCliAsync(params string[] command)
    {
        var writer = new StringWriter();
        var full = new List<string> { "--db", Path.Combine(root, "zhinu.db") };
        full.AddRange(command);
        var code = await CliApp.RunAsync([.. full], writer);
        return (code, writer.ToString());
    }
}
