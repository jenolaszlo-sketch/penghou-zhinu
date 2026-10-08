using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// `runs show` projects the run's recorded lineage and deadline, which the
/// durable run already carries but no operator surface exposed. A parent link is
/// child-workflow ancestry; a source link is fork lineage; absence is shown as
/// explicitly missing rather than blank.
/// </summary>
public sealed class RunShowLineageCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Show_ProjectsParentSourceAndDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var store = CreateStore();
        var parent = await CreateRunAsync(store, "parent", now, null, ct);
        var child = await CreateRunAsync(store, "child", now, parent, ct);
        var source = Guid.NewGuid();
        var forked = Guid.NewGuid();
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = forked,
            WorkflowName = "forked",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            SourceRunId = source,
            Deadline = now.AddHours(1)
        }, ct);

        var childShow = await RunCliAsync("runs", "show", child.ToString("D"));
        childShow.Code.Should().Be(0);
        childShow.Output.Should().Contain($"parent: {parent:D}");
        childShow.Output.Should().Contain("source: -");
        childShow.Output.Should().Contain("deadline: (none)");

        var forkedShow = await RunCliAsync("runs", "show", forked.ToString("D"));
        forkedShow.Code.Should().Be(0);
        forkedShow.Output.Should().Contain($"source: {source:D}");
        forkedShow.Output.Should().Contain($"deadline: {now.AddHours(1):O}");

        var json = await RunCliAsync("--format", "json", "runs", "show", child.ToString("D"));
        json.Code.Should().Be(0);
        json.Output.Should().Contain("\"Parent\"").And.Contain("\"Source\"").And.Contain("\"Deadline\"");

        async Task<(int Code, string Output)> RunCliAsync(params string[] command)
        {
            var writer = new StringWriter();
            var full = new List<string> { "--db", Path.Combine(root, "zhinu.db") };
            full.AddRange(command);
            var code = await CliApp.RunAsync([.. full], writer);
            return (code, writer.ToString());
        }
    }
}
