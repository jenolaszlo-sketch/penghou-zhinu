using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// AE-6.2: the durable authority-subject binding identifies whose current
/// authority governs a run's protected runtime effects. It is opaque identity
/// set once at admission — never a permission, never updated, never a stored
/// decision — and it survives process restart. Missing bindings fail closed at
/// effect time (proven by consumers, not here).
/// </summary>
public sealed class AuthoritySubjectBindingTests : WorkflowEngineTestBase
{
    private const string Binding = "tenant-1:subject-1:delegation-1:workspace-1";

    [Fact]
    public async Task Binding_Persists_And_Rereads()
    {
        var store = CreateStore();
        var id = await CreateRunAsync(store, Binding, TestContext.Current.CancellationToken);

        var run = await store.GetRunAsync(id, TestContext.Current.CancellationToken);

        run!.AuthoritySubjectBinding.Should().Be(Binding);
    }

    [Fact]
    public async Task Binding_Survives_Restart()
    {
        var first = CreateStore();
        var id = await CreateRunAsync(first, Binding, TestContext.Current.CancellationToken);

        // A new store instance over the same file is a process restart.
        var second = CreateStore();
        var run = await second.GetRunAsync(id, TestContext.Current.CancellationToken);

        run!.AuthoritySubjectBinding.Should().Be(Binding);
    }

    [Fact]
    public async Task NullBinding_Allowed_And_Readable()
    {
        var store = CreateStore();
        var id = await CreateRunAsync(store, null, TestContext.Current.CancellationToken);

        (await store.GetRunAsync(id, TestContext.Current.CancellationToken))!
            .AuthoritySubjectBinding.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyBinding_RejectedAtInsert(string binding)
    {
        var store = CreateStore();

        var act = () => CreateRunAsync(store, binding, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    [Fact]
    public async Task OversizedBinding_RejectedAtInsert()
    {
        var store = CreateStore();

        var act = () => CreateRunAsync(store, new string('b', 4097), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    [Fact]
    public async Task ControlCharacterBinding_RejectedAtInsert()
    {
        var store = CreateStore();

        var act = () => CreateRunAsync(store, "tenant:bind\ning", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    [Fact]
    public async Task Binding_Immutable_AcrossMetadataUpdate()
    {
        var workflow = new SingleStepWorkflow();
        var engine = CreateEngine(workflow, "binding-immutable");
        var id = await engine.StartAsync("binding-immutable", "1", "input",
            authoritySubjectBinding: Binding, cancellationToken: TestContext.Current.CancellationToken);

        await engine.UpdateRunMetadataAsync(id, new { note = "change" }, TestContext.Current.CancellationToken);
        var run = await engine.GetRunAsync(id, TestContext.Current.CancellationToken);

        run!.AuthoritySubjectBinding.Should().Be(Binding);
    }

    [Fact]
    public async Task StartAsync_FlowsBinding_ToRun_And_RejectsEmpty()
    {
        var workflow = new SingleStepWorkflow();
        var engine = CreateEngine(workflow, "binding-start");

        var id = await engine.StartAsync("binding-start", "1", "input",
            authoritySubjectBinding: Binding, cancellationToken: TestContext.Current.CancellationToken);
        (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!
            .AuthoritySubjectBinding.Should().Be(Binding);

        var act = () => engine.StartAsync("binding-start", "1", "input",
            authoritySubjectBinding: "  ", cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ConvergentStart_RequiresBindingEquality()
    {
        var workflow = new SingleStepWorkflow();
        var engine = CreateEngine(workflow, "binding-converge");
        var id = Guid.NewGuid();
        await engine.StartAsync("binding-converge", "1", "input", id,
            authoritySubjectBinding: Binding, cancellationToken: TestContext.Current.CancellationToken);

        // Same identity converges.
        var same = await engine.StartAsync("binding-converge", "1", "input", id,
            authoritySubjectBinding: Binding, cancellationToken: TestContext.Current.CancellationToken);
        same.Should().Be(id);

        // A different principal cannot converge onto the admitted run.
        var act = () => engine.StartAsync("binding-converge", "1", "input", id,
            authoritySubjectBinding: "tenant-1:mallory:other:workspace-1",
            cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task Fork_Inherits_SourceBinding()
    {
        var v1 = new TwoStepWorkflow();
        var v2 = new TwoStepWorkflow();
        var registry = new WorkflowRegistry()
            .Register("binding-fork", "1", v1)
            .Register("binding-fork", "2", v2);
        var engine = CreateEngine(registry);
        await engine.RunAsync<string, string>("binding-fork", "1", "goal",
            authoritySubjectBinding: Binding, cancellationToken: TestContext.Current.CancellationToken);
        var source = (await engine.GetRunAsync(v1.RunId, TestContext.Current.CancellationToken))!;
        source.AuthoritySubjectBinding.Should().Be(Binding);

        var forkedId = await engine.ForkAsync(source.Id, "finish",
            new ForkRunOptions { TargetWorkflowVersion = "2", Actor = "test", Reason = "binding inheritance" },
            TestContext.Current.CancellationToken);

        (await engine.GetRunAsync(forkedId, TestContext.Current.CancellationToken))!
            .AuthoritySubjectBinding.Should().Be(Binding);
    }

    [Fact]
    public async Task Migration_V6_To_V7_PreservesRuns_WithNullBinding()
    {
        var database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "migrate.db"),
            Pooling = false,
        });
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO workflow_runs (id, workflow_name, workflow_version, status,
                    created_at, updated_at, lease_generation)
                VALUES ($id, 'legacy', '1', 0, $now, $now, 1);
                """;
            command.Parameters.AddWithValue("$id", runId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        // Rewind to a v6 database: drop the new column and version.
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE workflow_runs DROP COLUMN authority_subject_binding;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE zhinu_schema SET version = 6 WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var migrated = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "migrate.db"),
            Pooling = false,
        });
        await migrated.InitializeAsync(TestContext.Current.CancellationToken);

        await using var verify = await migrated.OpenAsync(TestContext.Current.CancellationToken);
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "SELECT version FROM zhinu_schema WHERE id = 1;";
        Convert.ToInt32(await versionCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            .Should().Be(ZhinuSqliteSchema.CurrentVersion);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "migrate.db"),
            Pooling = false,
        });
        (await store.GetRunAsync(runId, TestContext.Current.CancellationToken))!
            .AuthoritySubjectBinding.Should().BeNull();
    }

    private static async Task<Guid> CreateRunAsync(
        SqliteWorkflowStore store, string? binding, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = id,
            WorkflowName = "binding",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            AuthoritySubjectBinding = binding,
        }, cancellationToken);
        return id;
    }

    private sealed class SingleStepWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("only", input, (value, _) => Task.FromResult($"S({value})"),
                cancellationToken: cancellationToken);
    }

    private sealed class TwoStepWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var a = await context.StepAsync("plan-a", input, (value, _) => Task.FromResult($"A({value})"),
                cancellationToken: cancellationToken);
            using (context.DependsOn("plan-a"))
            {
                return await context.StepAsync("finish", a, (value, _) => Task.FromResult($"F({value})"),
                    cancellationToken: cancellationToken);
            }
        }
    }
}
