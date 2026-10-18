using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupQueryTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DefinitionTraversal_IsIndependentOfRuntimeOccurrences_AndSupportsBoundedDeepManifest()
    {
        var manifest = DeepManifest(128);
        var store = CreateStore();
        var registry = new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-deep", Version = "1" }, () => new StaticWorkflow(), manifest));
        await using var engine = new WorkflowEngine(store, registry);
        var runId = await engine.StartAsync("query-deep", "1", "payload", cancellationToken: Ct);
        var reader = new WorkflowGroupReader(CreateStore());
        using (reader)
        {
            var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            snapshot.OccurrenceCount.Should().Be(0);
            snapshot.DefinitionCount.Should().Be(128);
            var collected = new List<WorkflowGroupDefinitionFact>();
            // Walk every parent definition and page each sibling set.
            var parents = new Queue<string?>();
            parents.Enqueue(null);
            while (parents.TryDequeue(out var currentParent))
            {
                string? cursor = null;
                do
                {
                    var page = await reader.QueryGroupDefinitionsAsync(runId, snapshot.SnapshotToken, currentParent, 1, cursor, Ct);
                    page.Status.Should().Be(WorkflowGroupReadStatus.Success);
                    foreach (var definition in page.Value!.Items)
                    {
                        collected.Add(definition);
                        parents.Enqueue(definition.GroupKey);
                    }
                    cursor = page.Value.NextCursor;
                } while (cursor is not null);
            }

            collected.Should().HaveCount(128);
            collected.Select(item => item.GroupKey).Should().OnlyHaveUniqueItems();
            collected.Should().OnlyContain(item => item.OccurrenceCount == 0);
            collected.Select(item => item.Kind).Should().Contain(ActivityGroupKind.Logical).And.Contain(ActivityGroupKind.Macro);
            testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new
            {
                scenario = "deep-definition-plane",
                runId,
                snapshot,
                definitionCount = collected.Count,
                maximumDepth = 128,
                allOccurrenceCountsZero = collected.All(item => item.OccurrenceCount == 0)
            }));
        }
    }

    [Fact]
    public async Task LegacyUngroupedRun_ReturnsSuccessfulEmptyGroupPlane()
    {
        var store = CreateStore();
        var registry = new WorkflowRegistry().Register("query-legacy", "1", new PlainWorkflow());
        await using var engine = new WorkflowEngine(store, registry);
        var runId = await engine.StartAsync("query-legacy", "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("plain:payload");

        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        snapshot.Status.Should().Be(WorkflowGroupReadStatus.Success);
        snapshot.Value!.Run.ManifestDigest.Should().BeNull();
        snapshot.Value.DefinitionCount.Should().Be(0);
        snapshot.Value.OccurrenceCount.Should().Be(0);
        var definitions = await reader.QueryGroupDefinitionsAsync(runId, snapshot.Value.SnapshotToken, cancellationToken: Ct);
        definitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
        definitions.Value!.Items.Should().BeEmpty();
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "legacy-ungrouped", runId, snapshot = snapshot.Value, definitions = definitions.Value }));
    }

    [Fact]
    public async Task RootChildrenMixOccurrencesAndUngroupedSteps_WhileOccurrenceChildrenStayStructural()
    {
        var runId = await CompleteAsync("query-mixed", new MixedWorkflow());
        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var rootNodes = await ReadAllAsync(reader, runId, snapshot.SnapshotToken, null, 1);
        rootNodes.Should().Contain(node => node.Kind == WorkflowGroupNodeKind.Occurrence);
        rootNodes.Should().Contain(node => node.Kind == WorkflowGroupNodeKind.UngroupedStep && node.Step!.StepKey == "plain");
        JsonSerializer.Serialize(rootNodes).Should().NotContain("payload");
        var root = rootNodes.Single(node => node.Kind == WorkflowGroupNodeKind.Occurrence).Occurrence!;
        var children = await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, root.OccurrenceId, pageSize: 20, cancellationToken: Ct);
        children.Status.Should().Be(WorkflowGroupReadStatus.Success);
        children.Value!.Items.Should().OnlyContain(node => node.Kind == WorkflowGroupNodeKind.Occurrence);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "mixed-root-and-structural-children", runId, snapshot, rootNodes, occurrenceChildren = children.Value }));
    }

    [Fact]
    public async Task RestartedStep_MembersDefaultToCurrentRevisionAndHistoryIsExplicit()
    {
        var store = CreateStore();
        var engine = new WorkflowEngine(store, GroupNestedWorkflow.Registry(new GroupNestedWorkflow()));
        Guid runId;
        await using (engine)
        {
            runId = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "payload", cancellationToken: Ct);
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("3");
            await engine.RestartStepAsync(runId, "$loop/outer/2/body/entry", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("3");
        }

        var currentStep = (await CreateStore().GetStepsAsync(runId, Ct)).Single(step => step.StepKey == "$loop/outer/2/body/entry");
        var membership = (await CreateStore().GetStepGroupMembershipAsync(currentStep.Id, Ct))!;
        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var current = await reader.QueryGroupMembersAsync(runId, snapshot.SnapshotToken, membership.GroupOccurrenceId, pageSize: 50, cancellationToken: Ct);
        var history = await reader.QueryGroupMembersAsync(runId, snapshot.SnapshotToken, membership.GroupOccurrenceId, includeHistory: true, pageSize: 50, cancellationToken: Ct);
        current.Status.Should().Be(WorkflowGroupReadStatus.Success);
        history.Status.Should().Be(WorkflowGroupReadStatus.Success);
        current.Value!.Items.Should().Contain(item => item.Step.StepKey == currentStep.StepKey && item.Step.Revision == 2 && item.Step.IsCurrent);
        current.Value.Items.Should().NotContain(item => item.Step.StepKey == currentStep.StepKey && item.Step.Revision == 1);
        history.Value!.Items.Should().Contain(item => item.Step.StepKey == currentStep.StepKey && item.Step.Revision == 1 && !item.Step.IsCurrent);
        history.Value.Items.Should().Contain(item => item.Step.StepKey == currentStep.StepKey && item.Step.Revision == 2 && item.Step.IsCurrent);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "member-current-versus-history", runId, occurrence = membership.GroupOccurrenceId, current = current.Value, history = history.Value }));
    }

    [Fact]
    public async Task ChildRunLinksRemainSeparateAndEachRunOwnsItsOwnGroupTree()
    {
        var store = CreateStore();
        var engine = new WorkflowEngine(store, GroupChildWorkflow.Registry());
        Guid parentId;
        await using (engine)
        {
            parentId = await engine.StartAsync(GroupChildWorkflow.ParentName, "1", "payload", cancellationToken: Ct);
            await engine.ExecuteAsync(parentId, Ct);
            (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct)).Should().Be("payload:child");
        }

        using var reader = new WorkflowGroupReader(CreateStore());
        var parentSnapshot = (await reader.GetRunGroupSnapshotAsync(parentId, Ct)).Value!;
        var parentNodes = await ReadAllAsync(reader, parentId, parentSnapshot.SnapshotToken, null, 50);
        parentSnapshot.SourceRun.Should().BeNull();
        parentSnapshot.Run.ParentRunId.Should().BeNull();
        parentSnapshot.Run.ManifestDigest.Should().Be(GroupChildWorkflow.Manifest().ManifestDigest);
        var childLink = parentNodes.Single(node => node.Kind == WorkflowGroupNodeKind.ChildRun).ChildRun!;
        childLink.ParentRunId.Should().Be(parentId);
        childLink.IsAvailable.Should().BeTrue();
        var childBoundaryMembers = (await ReadAllMembersAsync(reader, parentId, parentSnapshot.SnapshotToken, parentNodes))
            .Where(member => member.Step.ChildRunId == childLink.RunId).ToList();
        // A wait row is unnecessary when the child completes before the wait
        // step runs. The typed start result always supplies the durable link.
        var startMember = childBoundaryMembers.Single(member => member.Step.StepKey == "child:start");
        startMember.Step.GroupOccurrenceId.Should().NotBeNull();

        var childSnapshot = (await reader.GetRunGroupSnapshotAsync(childLink.RunId, Ct)).Value!;
        childSnapshot.Run.RunId.Should().Be(childLink.RunId);
        childSnapshot.Run.ParentRunId.Should().Be(parentId);
        var childRoot = (await reader.QueryGroupChildrenAsync(childLink.RunId, childSnapshot.SnapshotToken, pageSize: 50, cancellationToken: Ct)).Value!
            .Items.Single(node => node.Kind == WorkflowGroupNodeKind.Occurrence).Occurrence!;
        childRoot.WorkflowRunId.Should().Be(childLink.RunId);
        childRoot.OccurrenceId.Should().NotBe(startMember.Step.GroupOccurrenceId);
        (await reader.GetGroupOccurrenceAsync(childLink.RunId, parentSnapshot.SnapshotToken, childRoot.OccurrenceId, Ct)).Status
            .Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "child-run-link-and-independent-tree", parentSnapshot, parentNodes, childSnapshot, childRoot }));
    }

    [Fact]
    public async Task ReusedForkChildLinkRetainsActualSourceParentRun()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, GroupChildWorkflow.Registry());
        var sourceId = await engine.StartAsync(GroupChildWorkflow.ParentName, "1", "reuse-child", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
        var sourceChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = GroupChildWorkflow.ChildName }, Ct)).Single();

        var destinationId = await engine.ForkAsync(sourceId, "child:wait", new ForkRunOptions
        {
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(destinationId, Ct);
        await engine.WaitForCompletionAsync<string>(destinationId, cancellationToken: Ct);

        using var reader = new WorkflowGroupReader(CreateStore());
        var sourceSnapshot = (await reader.GetRunGroupSnapshotAsync(sourceId, Ct)).Value!;
        var destinationSnapshot = (await reader.GetRunGroupSnapshotAsync(destinationId, Ct)).Value!;
        sourceSnapshot.Run.ParentRunId.Should().BeNull();
        sourceSnapshot.SourceRun.Should().BeNull();
        destinationSnapshot.SourceRun!.RunId.Should().Be(sourceId, "the selected run source remains distinct from its reused child link");
        var sourceNodes = await ReadAllAsync(reader, sourceId, sourceSnapshot.SnapshotToken, null, 20);
        var sourceLink = sourceNodes.Single(node => node.Kind == WorkflowGroupNodeKind.ChildRun).ChildRun!;
        sourceLink.RunId.Should().Be(sourceChild.Id);
        sourceLink.ParentRunId.Should().Be(sourceId);
        var destNodes = await ReadAllAsync(reader, destinationId, destinationSnapshot.SnapshotToken, null, 20);
        var reusedLink = destNodes.Single(node => node.Kind == WorkflowGroupNodeKind.ChildRun).ChildRun!;
        reusedLink.RunId.Should().Be(sourceChild.Id);
        reusedLink.ParentRunId.Should().Be(sourceId,
            "a fork that reuses child:start must preserve the child run's actual native owner");
        reusedLink.ParentRunId.Should().NotBe(destinationId);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "fork-reused-child-link", sourceId, destinationId, childId = sourceChild.Id, sourceSnapshot, sourceNodes, destinationSnapshot, destNodes, reusedLink }));
    }

    [Fact]
    public async Task SnapshotCapturedBeforeWriterCommitKeepsItsGenerationForLaterPages()
    {
        var store = CreateStore();
        var workflow = new MixedWorkflow();
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-sensitive", Version = "1" }, () => workflow, MixedManifest());
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration));
        var runId = await engine.StartAsync("query-sensitive", "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);

        using var gate = new SnapshotGateDatabase(CreateDatabase(), runId);
        var reader = new WorkflowGroupReader(new SqliteWorkflowStore(gate));
        await using var writerConnection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await writerConnection.OpenAsync(Ct);
        using (reader)
        {
            var captureTask = Task.Run(async () => await reader.GetRunGroupSnapshotAsync(runId, Ct), Ct);
            try
            {
                await gate.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
                await using var transaction = await writerConnection.BeginTransactionAsync(Ct);
                await using (var updateStep = writerConnection.CreateCommand())
                {
                    updateStep.Transaction = (SqliteTransaction)transaction;
                    updateStep.CommandText = "UPDATE workflow_steps SET attempt = attempt + 1 WHERE workflow_run_id = $runId AND step_key = 'grouped' AND revision = (SELECT MAX(revision) FROM workflow_steps WHERE workflow_run_id = $runId AND step_key = 'grouped')";
                    updateStep.Parameters.AddWithValue("$runId", runId.ToString("D"));
                    (await updateStep.ExecuteNonQueryAsync(Ct)).Should().Be(1);
                }
                var updatedAt = DateTimeOffset.UtcNow.AddSeconds(5);
                await using (var updateRun = writerConnection.CreateCommand())
                {
                    updateRun.Transaction = (SqliteTransaction)transaction;
                    updateRun.CommandText = "UPDATE workflow_runs SET updated_at = $updatedAt WHERE id = $runId";
                    updateRun.Parameters.AddWithValue("$updatedAt", updatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    updateRun.Parameters.AddWithValue("$runId", runId.ToString("D"));
                    (await updateRun.ExecuteNonQueryAsync(Ct)).Should().Be(1);
                }
                await transaction.CommitAsync(Ct);
            }
            finally { gate.ReleaseRead.Set(); }

            var oldSnapshotResult = await captureTask;
            oldSnapshotResult.Status.Should().Be(WorkflowGroupReadStatus.Success, oldSnapshotResult.Reason);
            var oldSnapshot = oldSnapshotResult.Value!;
            var oldNodes = await ReadAllAsync(reader, runId, oldSnapshot.SnapshotToken, null, 20);
            var oldMembers = await ReadAllMembersAsync(reader, runId, oldSnapshot.SnapshotToken, oldNodes);
            oldMembers.Should().Contain(item => item.Step.StepKey == "grouped" && item.Step.Revision == 1 && item.Step.Attempt == 1 && item.Step.IsCurrent);
            oldMembers.Should().NotContain(item => item.Step.StepKey == "grouped" && item.Step.Attempt == 2);

            using var freshReader = new WorkflowGroupReader(CreateStore());
            var freshSnapshot = (await freshReader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var freshNodes = await ReadAllAsync(freshReader, runId, freshSnapshot.SnapshotToken, null, 20);
            var freshMembers = await ReadAllMembersAsync(freshReader, runId, freshSnapshot.SnapshotToken, freshNodes);
            freshMembers.Should().Contain(item => item.Step.StepKey == "grouped" && item.Step.Revision == 1 && item.Step.Attempt == 2 && item.Step.IsCurrent);
            freshSnapshot.ThroughDurableSequence.Should().Be(oldSnapshot.ThroughDurableSequence,
                "the writer changes snapshot-visible facts without appending workflow events");
            testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new
            {
                scenario = "sqlite-multiselect-snapshot-survives-writer-commit",
                runId,
                oldSnapshot,
                oldMembers,
                freshSnapshot,
                freshMembers,
                writerAttempt = 2
            }));
        }
    }

    [Fact]
    public async Task SnapshotOnlyGenerationChangeRetainsTheSameDurableWatermark()
    {
        var runId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        using var reader = new WorkflowGroupReader(CreateStore());
        var first = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var updatedAt = first.Run.UpdatedAt.AddSeconds(5);
        await UpdateRunUpdatedAtAsync(runId, updatedAt);
        var second = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;

        second.SnapshotToken.Should().NotBe(first.SnapshotToken);
        second.ThroughDurableSequence.Should().Be(first.ThroughDurableSequence,
            "the snapshot-visible run projection changed without a durable workflow event");
        first.Run.UpdatedAt.Should().NotBe(updatedAt);
        second.Run.UpdatedAt.Should().Be(updatedAt);
        var oldPage = await reader.QueryGroupChildrenAsync(runId, first.SnapshotToken, pageSize: 20, cancellationToken: Ct);
        var newPage = await reader.QueryGroupChildrenAsync(runId, second.SnapshotToken, pageSize: 20, cancellationToken: Ct);
        oldPage.Value!.SnapshotToken.Should().Be(first.SnapshotToken);
        newPage.Value!.SnapshotToken.Should().Be(second.SnapshotToken);
        oldPage.Value.ThroughDurableSequence.Should().Be(newPage.Value.ThroughDurableSequence);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "snapshot-only-generation-same-watermark", runId, first, second, updatedAt, oldPage = oldPage.Value, newPage = newPage.Value }));
    }

    [Fact]
    public async Task FanOutQueryReturnsEverySelectedOccurrenceAndEffectiveClosureAcrossRestart()
    {
        var store = CreateStore();
        var fanOutRegistration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-fanout", Version = "1" },
            () => new FanOutQueryWorkflow(), FanOutQueryWorkflow.Manifest());
        var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(fanOutRegistration));
        Guid runId;
        await using (engine)
        {
            runId = await engine.StartAsync("query-fanout", "1", "payload", cancellationToken: Ct);
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("A:B");
            await engine.RestartStepAsync(runId, "items.a", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        }

        using var reader = new WorkflowGroupReader(CreateStore());
        var incompleteResult = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        incompleteResult.Status.Should().Be(WorkflowGroupReadStatus.Success, incompleteResult.Reason);
        var incompleteSnapshot = incompleteResult.Value!;
        var incompletePage = await reader.QueryGroupExpansionsAsync(runId, incompleteSnapshot.SnapshotToken, pageSize: 10, cancellationToken: Ct);
        incompletePage.Value!.Items.Should().ContainSingle();
        var beforeRecovery = incompletePage.Value.Items.Single();
        beforeRecovery.Items.Should().HaveCount(2);
        beforeRecovery.Items.Select(item => item.OccurrenceId).Should().OnlyHaveUniqueItems();
        beforeRecovery.IsCurrent.Should().BeTrue();
        beforeRecovery.IsComplete.Should().BeFalse();
        beforeRecovery.Closures.Should().ContainSingle();

        await using (var resume = new WorkflowEngine(CreateStore(), new WorkflowRegistry().Register(fanOutRegistration)))
        {
            await resume.ExecuteAsync(runId, Ct);
            (await resume.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("A:B");
        }
        var completeSnapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var complete = (await reader.QueryGroupExpansionsAsync(runId, completeSnapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
        complete.IsComplete.Should().BeTrue();
        complete.Closures.Should().HaveCount(2);
        complete.Items.Select(item => item.OccurrenceId).Should().BeEquivalentTo(beforeRecovery.Items.Select(item => item.OccurrenceId));
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "fanout-selected-members-and-effective-closure", incompleteSnapshot, beforeRecovery, completeSnapshot, complete }));
    }

    [Fact]
    public async Task ProvenanceIsRedactedByDefaultAndPermissionChangesInvalidatePrivilegedSnapshot()
    {
        var runId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        var store = CreateStore();
        var authorizer = new MutableAuthorizer(WorkflowGroupReadAccess.Structure | WorkflowGroupReadAccess.Provenance | WorkflowGroupReadAccess.SourceReference);
        using var privileged = new WorkflowGroupReader(store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { IncludeProvenance = true, IncludeSourceReferences = true }
        }, authorizer: authorizer);
        var privilegedSnapshot = (await privileged.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        privilegedSnapshot.ProvenanceIncluded.Should().BeTrue();
        var definitions = (await privileged.QueryGroupDefinitionsAsync(runId, privilegedSnapshot.SnapshotToken, parentGroupKey: "root", cancellationToken: Ct)).Value!;
        definitions.Items.Single(item => item.GroupKey == "macro").Provenance!.SourceReference.Should().Be("src/workflow.cs");
        authorizer.Calls.Should().BeGreaterThan(1, "authorization is re-evaluated on each query, not inherited from the snapshot token");

        authorizer.Access = WorkflowGroupReadAccess.Structure;
        var changed = await privileged.QueryGroupDefinitionsAsync(runId, privilegedSnapshot.SnapshotToken, cancellationToken: Ct);
        changed.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        using var defaultReader = new WorkflowGroupReader(CreateStore());
        var redactedSnapshot = (await defaultReader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var redacted = (await defaultReader.QueryGroupDefinitionsAsync(runId, redactedSnapshot.SnapshotToken, parentGroupKey: "root", cancellationToken: Ct)).Value!;
        redacted.Items.Single(item => item.GroupKey == "macro").Provenance.Should().BeNull();
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "provenance-redaction-and-permission-drop", runId, privilegedSnapshot, definitions, changedStatus = changed.Status, redactedSnapshot, redacted }));
    }

    [Fact]
    public async Task QueryTokensAreReaderAndRunBound_AndPermissionDenialIsExplicit()
    {
        var firstId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        var secondId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        using var firstReader = new WorkflowGroupReader(CreateStore());
        using var secondReader = new WorkflowGroupReader(CreateStore());
        var firstSnapshot = (await firstReader.GetRunGroupSnapshotAsync(firstId, Ct)).Value!;
        (await secondReader.QueryGroupDefinitionsAsync(firstId, firstSnapshot.SnapshotToken, cancellationToken: Ct)).Status
            .Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        (await firstReader.QueryGroupDefinitionsAsync(secondId, firstSnapshot.SnapshotToken, cancellationToken: Ct)).Status
            .Should().Be(WorkflowGroupReadStatus.ResyncRequired);

        using var denied = new WorkflowGroupReader(CreateStore(), authorizer: new MutableAuthorizer(WorkflowGroupReadAccess.None));
        (await denied.GetRunGroupSnapshotAsync(firstId, Ct)).Status.Should().Be(WorkflowGroupReadStatus.Forbidden);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "reader-run-binding-and-denied-access", firstId, secondId, token = firstSnapshot.SnapshotToken }));
    }

    [Fact]
    public async Task ExpiredAndEvictedSnapshotsRequireResync()
    {
        var firstId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        var secondId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        var clock = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var options = new WorkflowGroupQueryOptions { SnapshotLifetime = TimeSpan.FromSeconds(1), MaximumSnapshots = 1 };
        using var reader = new WorkflowGroupReader(CreateStore(), options, clock);
        var expired = (await reader.GetRunGroupSnapshotAsync(firstId, Ct)).Value!;
        clock.Advance(TimeSpan.FromSeconds(2));
        (await reader.QueryGroupDefinitionsAsync(firstId, expired.SnapshotToken, cancellationToken: Ct)).Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);

        clock.Advance(TimeSpan.FromSeconds(2));
        var evicted = (await reader.GetRunGroupSnapshotAsync(firstId, Ct)).Value!;
        _ = await reader.GetRunGroupSnapshotAsync(secondId, Ct);
        (await reader.QueryGroupDefinitionsAsync(firstId, evicted.SnapshotToken, cancellationToken: Ct)).Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "expiry-and-cache-eviction", firstId, secondId, expired = expired.SnapshotToken, evicted = evicted.SnapshotToken }));
    }

    [Fact]
    public async Task InvalidAndMismatchedCursorsRequireResync_AndPageSizeIsBounded()
    {
        var runId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var definitions = await reader.QueryGroupDefinitionsAsync(runId, snapshot.SnapshotToken, parentGroupKey: "root", pageSize: 1, cancellationToken: Ct);
        definitions.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var children = await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, pageSize: 1, cancellationToken: Ct);
        children.Status.Should().Be(WorkflowGroupReadStatus.Success);
        children.Value!.NextCursor.Should().NotBeNull();
        (await reader.QueryGroupDefinitionsAsync(runId, snapshot.SnapshotToken, cursor: "not-a-cursor", cancellationToken: Ct)).Status
            .Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cursor: children.Value.NextCursor, cancellationToken: Ct)).Status
            .Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        foreach (var size in new[] { 0, 1001 })
        {
            var call = () => reader.QueryGroupDefinitionsAsync(runId, snapshot.SnapshotToken, pageSize: size, cancellationToken: Ct).AsTask();
            await call.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "cursor-binding-and-page-size", runId, snapshotToken = snapshot.SnapshotToken, definitions = definitions.Value }));
    }

    [Theory]
    [InlineData(1, 33_554_432L)]
    [InlineData(262_144, 1L)]
    public async Task SnapshotReadLimitsReturnNoPartialValue(int maximumRows, long maximumBytes)
    {
        var runId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        using var reader = new WorkflowGroupReader(CreateStore(), new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { MaximumRows = maximumRows, MaximumBytes = maximumBytes }
        });
        var result = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        result.Status.Should().Be(WorkflowGroupReadStatus.LimitExceeded);
        result.Value.Should().BeNull();
        result.Reason.Should().NotBeNullOrWhiteSpace();
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "snapshot-bounded-read", runId, maximumRows, maximumBytes, result.Status, result.Reason }));
    }

    [Fact]
    public async Task CancellationIsPassedThroughTheReadAuthorizationBoundary()
    {
        var runId = await CompleteAsync("query-sensitive", new MixedWorkflow());
        var authorizer = new MutableAuthorizer(WorkflowGroupReadAccess.Structure);
        using var reader = new WorkflowGroupReader(CreateStore(), authorizer: authorizer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = () => reader.GetRunGroupSnapshotAsync(runId, cancellation.Token).AsTask();
        await query.Should().ThrowAsync<OperationCanceledException>();
        authorizer.Calls.Should().Be(0, "the query observes cancellation before authorization or storage work");
        testOutput.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "read-cancellation", runId, authorizationCalls = authorizer.Calls }));
    }

    private async Task<Guid> CompleteAsync(string name, IWorkflow<string, string> workflow, ActivityGroupManifest? manifest = null)
    {
        var definition = new WorkflowDefinition { Name = name, Version = "1" };
        var actualManifest = manifest ?? (workflow is MixedWorkflow ? MixedManifest() : null);
        var registration = actualManifest is null
            ? new WorkflowRegistration<string, string>(definition, () => workflow)
            : new WorkflowRegistration<string, string>(definition, () => workflow, actualManifest);
        var engine = new WorkflowEngine(CreateStore(), new WorkflowRegistry().Register(registration));
        await using (engine)
        {
            var runId = await engine.StartAsync(name, "1", "payload", cancellationToken: Ct);
            await engine.ExecuteAsync(runId, Ct);
            await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
            return runId;
        }
    }

    private async Task UpdateRunUpdatedAtAsync(Guid runId, DateTimeOffset updatedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workflow_runs SET updated_at = $updatedAt WHERE id = $runId";
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
    }

    private SqliteDatabase CreateDatabase() => new(new ZhinuSqliteOptions
    {
        DatabasePath = Path.Combine(root, "zhinu.db"),
        BusyTimeout = TimeSpan.FromSeconds(2),
        Pooling = false
    });

    private static async Task<List<WorkflowGroupNode>> ReadAllAsync(
        IWorkflowGroupReader reader, Guid runId, string snapshotToken, string? parentOccurrenceId, int pageSize)
    {
        var nodes = new List<WorkflowGroupNode>();
        string? cursor = null;
        do
        {
            var page = await reader.QueryGroupChildrenAsync(runId, snapshotToken, parentOccurrenceId, pageSize, cursor, Ct);
            page.Status.Should().Be(WorkflowGroupReadStatus.Success);
            nodes.AddRange(page.Value!.Items);
            cursor = page.Value.NextCursor;
        } while (cursor is not null);
        return nodes;
    }

    private static async Task<List<WorkflowGroupMember>> ReadAllMembersAsync(
        IWorkflowGroupReader reader, Guid runId, string snapshotToken, IReadOnlyList<WorkflowGroupNode> rootNodes)
    {
        var members = new List<WorkflowGroupMember>();
        var pending = new Queue<WorkflowGroupOccurrenceFact>(rootNodes
            .Where(node => node.Kind == WorkflowGroupNodeKind.Occurrence)
            .Select(node => node.Occurrence!));
        while (pending.TryDequeue(out var occurrence))
        {
            string? cursor = null;
            do
            {
                var page = await reader.QueryGroupMembersAsync(runId, snapshotToken, occurrence.OccurrenceId,
                    pageSize: 50, cursor: cursor, cancellationToken: Ct);
                page.Status.Should().Be(WorkflowGroupReadStatus.Success);
                members.AddRange(page.Value!.Items);
                cursor = page.Value.NextCursor;
            } while (cursor is not null);

            var children = await ReadAllAsync(reader, runId, snapshotToken, occurrence.OccurrenceId, 50);
            foreach (var child in children.Where(node => node.Kind == WorkflowGroupNodeKind.Occurrence))
                pending.Enqueue(child.Occurrence!);
        }
        return members;
    }

    private static ActivityGroupManifest DeepManifest(int count)
    {
        var definitions = new List<ActivityGroupDefinition>(count);
        for (var index = 0; index < count; index++)
        {
            var key = "g" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var parent = index == 0 ? null : "g" + (index - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var kind = index % 2 == 0 ? ActivityGroupKind.Logical : ActivityGroupKind.Macro;
            var provenance = kind == ActivityGroupKind.Macro
                ? new ActivityGroupMacroProvenance("Macro" + index, "fingerprint", "invoke" + index)
                : null;
            definitions.Add(new ActivityGroupDefinition(key, kind, "Group " + index, parent, provenance: provenance));
        }
        return ActivityGroupManifest.Create(definitions);
    }

    private static ActivityGroupManifest MixedManifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("macro", ActivityGroupKind.Macro, "Macro", "root",
            provenance: new ActivityGroupMacroProvenance("Macro", "source-sensitive-fingerprint", "invocation", "1", "src/workflow.cs"))
    ]);

    private sealed class StaticWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class PlainWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("plain", input, (value, _) => Task.FromResult("plain:" + value), cancellationToken: cancellationToken);
    }

    private sealed class MixedWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var macro = context.GetActivityGroup("macro", root);
            await context.StepAsync("grouped", input, (value, _) => Task.FromResult(value),
                new StepOptions { Group = new ActivityGroupStepBinding(new ActivityGroupMembership("macro"), macro) }, cancellationToken);
            return await context.StepAsync("plain", input, (value, _) => Task.FromResult("plain:" + value), cancellationToken: cancellationToken);
        }
    }

    private sealed class FanOutQueryWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var selection = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: cancellationToken);
            var items = await context.FanOutAsync("items", selection, value => value,
                (value, _, _) => Task.FromResult(value.ToUpperInvariant()),
                new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
                {
                    DependsOn = ["select"],
                    FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root"))
                }, cancellationToken);
            return await context.StepAsync("finish", string.Join(':', items), (value, _) => Task.FromResult(value), cancellationToken: cancellationToken);
        }

        internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root",
                provenance: new ActivityGroupMacroProvenance("Work", "fanout", "items"))
        ]);
    }

    private sealed class MutableAuthorizer(WorkflowGroupReadAccess access) : IWorkflowGroupReadAuthorizer
    {
        public WorkflowGroupReadAccess Access { get; set; } = access;
        public int Calls;
        public ValueTask<WorkflowGroupReadAccess> AuthorizeAsync(Guid workflowRunId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Access);
        }
    }

    private sealed class SnapshotGateDatabase(IZhinuSqliteDatabase inner, Guid targetRunId) : IZhinuSqliteDatabase, IDisposable
    {
        private int entered;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseRead { get; } = new(false);
        public ZhinuSqliteOptions Options => inner.Options;
        public TimeProvider TimeProvider => inner.TimeProvider;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default) => inner.EnsureInitializedAsync(cancellationToken);
        public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) => inner.CheckpointAsync(cancellationToken);
        public ValueTask VacuumAsync(CancellationToken cancellationToken = default) => inner.VacuumAsync(cancellationToken);

        public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenAsync(cancellationToken);
            try
            {
                connection.CreateFunction<string, int>("group_query_gate", id =>
                {
                    if (Guid.TryParse(id, out var parsed) && parsed == targetRunId && Interlocked.Exchange(ref entered, 1) == 0)
                    {
                        ReadEntered.TrySetResult();
                        if (!ReleaseRead.Wait(TimeSpan.FromSeconds(20)))
                            throw new TimeoutException("Snapshot read gate was not released in time.");
                    }
                    return 1;
                });
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TEMP VIEW workflow_runs AS SELECT * FROM main.workflow_runs WHERE group_query_gate(id) = 1";
                await command.ExecuteNonQueryAsync(cancellationToken);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public void Dispose() => ReleaseRead.Dispose();
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
