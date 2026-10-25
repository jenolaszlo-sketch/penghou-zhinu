using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class WorkflowGroupReaderTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SnapshotIndexMemoryIsChargedBeforeAdmission()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, estimatedBytes: 65_536 - 1024));
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { MaximumBytes = 65_536 },
            MaximumCacheBytes = 65_536
        }, new FakeTimeProvider(Epoch));
        var result = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        result.Status.Should().Be(WorkflowGroupReadStatus.LimitExceeded);
        result.Value.Should().BeNull("the data fits but its required indexes do not fit the cache");
    }

    [Theory]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData(StepStatus.Completed)]
    [InlineData(StepStatus.Failed)]
    [InlineData(StepStatus.Waiting)]
    [InlineData(StepStatus.Cancelled)]
    public async Task IndexedStatesMatchPureReductionAndRemainFrozenAcrossConcurrentReads(StepStatus status)
    {
        var runId = Guid.NewGuid();
        var rootId = ActivityGroupOccurrence.ComputeId(runId, "root", null, "[]");
        var leafId = ActivityGroupOccurrence.ComputeId(runId, "leaf", rootId, "[]");
        var work = new WorkflowGroupStepFact(runId, Guid.NewGuid(), "work", 2, 3, status,
            true, leafId, ActivityGroupMemberRole.Work, 1, Epoch.AddMinutes(1), null, Epoch, null, null);
        var control = work with { StepId = Guid.NewGuid(), StepKey = "control", Role = ActivityGroupMemberRole.Control, Status = StepStatus.Completed };
        var steps = new List<WorkflowGroupStepFact>
        {
            work with { StepId = Guid.NewGuid(), Revision = 1, Status = StepStatus.Failed, IsCurrent = false },
            work, control,
            work with { StepId = Guid.NewGuid(), StepKey = "outside", GroupOccurrenceId = null, Role = null, Status = StepStatus.Pending }
        };
        var data = Snapshot(runId, definitions:
            [Definition("root", null, 0) with { OccurrenceCount = 1 }, Definition("leaf", "root", 0) with { OccurrenceCount = 1 }],
            occurrences: [new(runId, rootId, "root", null, "[]"), new(runId, leafId, "leaf", rootId, "[]")], steps: steps) with
        {
            Run = Snapshot(runId).Run with { Status = WorkflowStatus.Completed },
            Dependencies = [new("work", "outside")],
            Waits = status == StepStatus.Waiting ? [new(Guid.NewGuid(), work.StepId, "work", 2, WaitKind.Signal, WaitStatus.Ready, null, null, null)] : []
        };
        var expected = WorkflowGroupStateReducer.Evaluate(new WorkflowGroupStateInput
        {
            Run = data.Run,
            Members = [new("work", ActivityGroupMemberRole.Work), new("control", ActivityGroupMemberRole.Control)],
            Steps = steps.ToArray(),
            Dependencies = data.Dependencies,
            Waits = data.Waits,
            Extent = WorkflowGroupExtent.Closed
        }, Epoch);
        using var reader = Reader(NewStore(data).Store, time: new FakeTimeProvider(Epoch));
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        steps.Clear();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, index % 2 == 0 ? rootId : leafId, Ct).AsTask()));
        foreach (var result in results)
        {
            result.Status.Should().Be(WorkflowGroupReadStatus.Success);
            result.Value!.State.Should().BeEquivalentTo(expected);
            result.Value.ObservedAt.Should().Be(Epoch);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EffectiveClosureRequiresItsActualVectorDigest(bool correctDigest)
    {
        var runId = Guid.NewGuid();
        var id = ActivityGroupOccurrence.ComputeId(runId, "work", null, "[]");
        var selection = new WorkflowGroupStepFact(runId, Guid.NewGuid(), "select", 1, 1, StepStatus.Completed,
            true, null, null, 1, null, null, Epoch, Epoch, null);
        var item = selection with { StepId = Guid.NewGuid(), StepKey = "item", GroupOccurrenceId = id, Role = ActivityGroupMemberRole.Work };
        var json = JsonSerializer.Serialize(new[] { JsonSerializer.Serialize(new { key = "a", step = item.StepId.ToString("D"), revision = 1 }) });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("penghou.zhinu.activity-group-fanout-closure.v1\n" + json))).ToLowerInvariant();
        var data = Snapshot(runId, definitions: [Definition("work", null, 0) with { OccurrenceCount = 1 }],
            occurrences: [new(runId, id, "work", null, "[]")], steps: [selection, item], expansions:
            [new("expansion", "items", selection.StepId, 1, "plan", 1, true, true,
                [new("a", "item", id, ActivityGroupMemberRole.Work)],
                [new(correctDigest ? digest : "incorrect-digest", 1, Epoch, [new(item.StepId, 1)])])]);
        using var reader = Reader(NewStore(data).Store, time: new FakeTimeProvider(Epoch));
        var result = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        result.Status.Should().Be(correctDigest ? WorkflowGroupReadStatus.Success : WorkflowGroupReadStatus.InconsistentData);
    }

    [Theory]
    [InlineData("null-definition")]
    [InlineData("null-expansion")]
    [InlineData("null-items")]
    [InlineData("null-closure")]
    [InlineData("duplicate-step-id")]
    [InlineData("missing-boundary")]
    [InlineData("false-completion")]
    [InlineData("missing-item")]
    public async Task MalformedProviderFactsCannotBecomeSuccessfulSnapshots(string corruption)
    {
        var runId = Guid.NewGuid();
        var step = new WorkflowGroupStepFact(runId, Guid.NewGuid(), "select", 1, 1, StepStatus.Completed, true,
            null, null, 1, null, null, Epoch, Epoch, null);
        var expansion = new WorkflowGroupExpansionFact("expansion", "items", step.StepId, 1, "digest", 0, true, false, [], []);
        var data = Snapshot(runId, steps: [step], expansions: [expansion]);
        data = corruption switch
        {
            "null-definition" => data with { Definitions = [null!] },
            "null-expansion" => data with { Expansions = [null!] },
            "null-items" => data with { Expansions = [expansion with { Items = null! }] },
            "null-closure" => data with { Expansions = [expansion with { Closures = [null!] }] },
            "duplicate-step-id" => data with { Steps = [step, step with { StepKey = "other", Revision = 2 }] },
            "missing-boundary" => data with { Expansions = [expansion with { BoundaryStepId = Guid.NewGuid() }] },
            "false-completion" => data with { Expansions = [expansion with { IsComplete = true }] },
            _ => data with { Expansions = [expansion with { ItemCount = 1 }] }
        };
        using var reader = Reader(NewStore(data).Store, time: new FakeTimeProvider(Epoch));
        var result = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        result.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotFreezesProviderCollectionsAndReturnsReadOnlyPages()
    {
        var runId = Guid.NewGuid();
        var definitions = new List<WorkflowGroupDefinitionFact>
        {
            Definition("alpha", null, 0),
            Definition("beta", null, 1)
        };
        var data = Snapshot(runId, definitions: definitions);
        var proxy = NewStore(data);
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        opened.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var token = opened.Value!.SnapshotToken;
        var first = await reader.QueryGroupDefinitionsAsync(runId, token, pageSize: 1, cancellationToken: Ct);
        first.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var firstPage = first.Value!;
        firstPage.Items.Should().ContainSingle().Which.GroupKey.Should().Be("alpha");

        definitions.Clear();
        definitions.Add(Definition("later", null, 0));
        var second = await reader.QueryGroupDefinitionsAsync(runId, token, pageSize: 1, cursor: firstPage.NextCursor, cancellationToken: Ct);

        second.Status.Should().Be(WorkflowGroupReadStatus.Success);
        second.Value!.Items.Should().ContainSingle().Which.GroupKey.Should().Be("beta");
        var mutableView = second.Value.Items.Should().BeAssignableTo<IList<WorkflowGroupDefinitionFact>>().Subject;
        mutableView.IsReadOnly.Should().BeTrue();
        Action mutation = () => mutableView[0] = Definition("mutated", null, 0);
        mutation.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("cross-run")]
    [InlineData("orphan")]
    [InlineData("cycle")]
    public async Task InvalidOccurrenceGraphIsReportedAsInconsistent(string shape)
    {
        var runId = Guid.NewGuid();
        var occurrences = shape switch
        {
            "cross-run" => new[]
            {
                new WorkflowGroupOccurrenceFact(Guid.NewGuid(), "foreign", "root", null, "[]")
            },
            "orphan" => new[]
            {
                new WorkflowGroupOccurrenceFact(runId, "orphan", "root", "missing", "[]")
            },
            _ => new[]
            {
                new WorkflowGroupOccurrenceFact(runId, "a", "root", "b", "[]"),
                new WorkflowGroupOccurrenceFact(runId, "b", "root", "a", "[]")
            }
        };
        var proxy = NewStore(Snapshot(runId, definitions: [Definition("root", null, 0)], occurrences: occurrences));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));

        var result = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
    }

    [Fact]
    public async Task DuplicateSiblingOrdinalsAreInconsistent()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, definitions:
        [
            Definition("first", null, 0),
            Definition("second", null, 0)
        ]));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task IncorrectDefinitionOccurrenceCountIsInconsistent(int occurrenceCount)
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, definitions: [Definition("root", null, 0) with { OccurrenceCount = occurrenceCount }]));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    public async Task InvalidChildRunLinksAreInconsistent(string shape)
    {
        var runId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var children = shape == "empty-id"
            ? new[] { new WorkflowGroupChildRunFact(Guid.Empty, runId, WorkflowStatus.Pending, true) }
            : new[]
            {
                new WorkflowGroupChildRunFact(childId, runId, WorkflowStatus.Pending, true),
                new WorkflowGroupChildRunFact(childId, runId, WorkflowStatus.Pending, true)
            };
        var proxy = NewStore(Snapshot(runId, childRuns: children));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
    }

    [Fact]
    public async Task ConcurrentOpensNeverRetainMoreThanConfiguredSnapshotCount()
    {
        var runIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var snapshots = runIds.ToDictionary(id => id, id => Snapshot(id));
        var proxy = NewStore(snapshots);
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            MaximumSnapshots = 2,
            MaximumCacheBytes = 1024 * 1024,
            ReadLimits = new WorkflowGroupReadLimits { MaximumBytes = 1024 * 1024 }
        }, new FakeTimeProvider(Epoch));

        var opened = await Task.WhenAll(runIds.Select(id => reader.GetRunGroupSnapshotAsync(id, Ct).AsTask()));
        opened.Should().OnlyContain(result => result.Status == WorkflowGroupReadStatus.Success);
        var followups = await Task.WhenAll(opened.Select((result, index) => reader
            .QueryGroupDefinitionsAsync(runIds[index], result.Value!.SnapshotToken, cancellationToken: Ct).AsTask()));

        followups.Count(result => result.Status == WorkflowGroupReadStatus.Success).Should().Be(2);
        followups.Should().OnlyContain(result => result.Status == WorkflowGroupReadStatus.Success ||
            result.Status == WorkflowGroupReadStatus.ResyncRequired);
        proxy.ReadCalls.Should().Be(6);
    }

    [Fact]
    public async Task OverBudgetSnapshotIsRejectedAndNeverCached()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, estimatedBytes: 2));
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { MaximumBytes = 1 },
            MaximumCacheBytes = 1
        }, new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.LimitExceeded);
        opened.Value.Should().BeNull();
    }

    [Fact]
    public async Task ActualSafeProjectionBytesAreBoundedEvenWhenProviderEstimateIsSmall()
    {
        var runId = Guid.NewGuid();
        var data = Snapshot(runId, estimatedBytes: 1, workflowName: new string('w', 2048));
        var proxy = NewStore(data);
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { MaximumBytes = 1024 },
            MaximumCacheBytes = 1024
        }, new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.LimitExceeded);
        opened.Value.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredSnapshotTokenRequiresResynchronization()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        var clock = new FakeTimeProvider(Epoch);
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            SnapshotLifetime = TimeSpan.FromSeconds(1)
        }, clock);
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        opened.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var token = opened.Value!.SnapshotToken;

        clock.Advance(TimeSpan.FromSeconds(1));
        var page = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);

        page.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    [Fact]
    public async Task CachedExpansionAndClosureNestedCollectionsAreDefensivelyFrozen()
    {
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var closureSteps = new List<WorkflowGroupRevisionFact>();
        var closures = new List<WorkflowGroupClosureFact>
        {
            new(Convert.ToHexString(SHA256.HashData("penghou.zhinu.activity-group-fanout-closure.v1\n[]"u8)).ToLowerInvariant(), 0, Epoch, closureSteps)
        };
        var items = new List<WorkflowGroupFanOutItemFact>();
        var expansions = new List<WorkflowGroupExpansionFact>
        {
            new("expansion", "items", stepId, 1, new string('b', 64), 0, true, true, items, closures)
        };
        var steps = new[]
        {
            new WorkflowGroupStepFact(runId, stepId, "select", 1, 1, StepStatus.Completed, true,
                null, null, 0, null, null, Epoch, Epoch, null)
        };
        var proxy = NewStore(Snapshot(runId, expansions: expansions, steps: steps));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        opened.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var token = opened.Value!.SnapshotToken;

        items.Add(new WorkflowGroupFanOutItemFact("late", "item", "occurrence", ActivityGroupMemberRole.Work));
        closureSteps.Add(new WorkflowGroupRevisionFact(Guid.NewGuid(), 1));
        closures.Clear();
        var result = await reader.QueryGroupExpansionsAsync(runId, token, cancellationToken: Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var expansion = result.Value!.Items.Should().ContainSingle().Which;
        expansion.Items.Should().BeEmpty();
        expansion.Closures.Should().ContainSingle();
        expansion.Closures[0].Steps.Should().BeEmpty();
        expansion.Items.Should().BeAssignableTo<IList<WorkflowGroupFanOutItemFact>>().Subject.IsReadOnly.Should().BeTrue();
        expansion.Closures.Should().BeAssignableTo<IList<WorkflowGroupClosureFact>>().Subject.IsReadOnly.Should().BeTrue();
        expansion.Closures[0].Steps.Should().BeAssignableTo<IList<WorkflowGroupRevisionFact>>().Subject.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task MissingOptionalCapabilityReturnsUnsupportedWithoutStorageFallback()
    {
        var store = DispatchProxy.Create<IWorkflowStore, LegacyStoreProxy>();
        var proxy = (LegacyStoreProxy)(object)store;
        using var reader = Reader(store, time: new FakeTimeProvider(Epoch));

        var result = await reader.GetRunGroupSnapshotAsync(Guid.NewGuid(), Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.Unsupported);
        proxy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SuccessfulProviderResultWithoutSnapshotIsInconsistent()
    {
        var runId = Guid.NewGuid();
        var store = DispatchProxy.Create<IGroupReaderTestStore, StoreProxy>();
        var proxy = (StoreProxy)(object)store;
        proxy.Snapshots = new Dictionary<Guid, WorkflowGroupSnapshotData> { [runId] = Snapshot(runId) };
        proxy.ReturnSuccessWithNullSnapshot = true;
        using var reader = Reader(store, time: new FakeTimeProvider(Epoch));

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        opened.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
    }

    [Fact]
    public async Task CancellationIsObservedBeforeAuthorizationOrRepositoryAccess()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        var authorizer = new CountingAuthorizer(WorkflowGroupReadAccess.Structure);
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch), authorizer: authorizer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var call = () => reader.GetRunGroupSnapshotAsync(runId, cancellation.Token).AsTask();

        await call.Should().ThrowAsync<OperationCanceledException>();
        authorizer.Calls.Should().Be(0);
        proxy.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task AuthorizationIsCheckedOnEveryPageAndRevocationInvalidatesSensitiveSnapshot()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, definitions: [Definition("macro", null, 0, provenance: true)]));
        var authorizer = new CountingAuthorizer(WorkflowGroupReadAccess.Structure | WorkflowGroupReadAccess.Provenance);
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { IncludeProvenance = true }
        }, new FakeTimeProvider(Epoch), authorizer);

        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;
        var firstPage = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);
        opened.Status.Should().Be(WorkflowGroupReadStatus.Success);
        firstPage.Status.Should().Be(WorkflowGroupReadStatus.Success);
        authorizer.Calls.Should().Be(2);

        authorizer.Access = WorkflowGroupReadAccess.Structure;
        var revoked = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);
        revoked.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    [Fact]
    public async Task LiveStructuralRevocationReturnsForbidden()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        var authorizer = new CountingAuthorizer(WorkflowGroupReadAccess.Structure);
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch), authorizer: authorizer);
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;

        authorizer.Access = WorkflowGroupReadAccess.None;
        var denied = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);

        denied.Status.Should().Be(WorkflowGroupReadStatus.Forbidden);
    }

    [Fact]
    public async Task SourceReferencePermissionDropInvalidatesSnapshot()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId, definitions: [Definition("macro", null, 0, provenance: true)]));
        var authorizer = new CountingAuthorizer(WorkflowGroupReadAccess.Structure | WorkflowGroupReadAccess.Provenance |
            WorkflowGroupReadAccess.SourceReference);
        using var reader = Reader(proxy.Store, new WorkflowGroupQueryOptions
        {
            ReadLimits = new WorkflowGroupReadLimits { IncludeProvenance = true, IncludeSourceReferences = true }
        }, new FakeTimeProvider(Epoch), authorizer);
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;
        opened.Value.SourceReferencesIncluded.Should().BeTrue();

        authorizer.Access = WorkflowGroupReadAccess.Structure | WorkflowGroupReadAccess.Provenance;
        var revoked = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);

        revoked.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    [Fact]
    public async Task MissingValidOccurrenceReturnsOccurrenceNotFound()
    {
        var runId = Guid.NewGuid();
        var data = Snapshot(runId,
            definitions: [Definition("root", null, 0) with { OccurrenceCount = 1 }],
            occurrences: [new WorkflowGroupOccurrenceFact(runId, ActivityGroupOccurrence.ComputeId(runId, "root", null, "[]"), "root", null, "[]")]);
        var proxy = NewStore(data);
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);

        var missing = await reader.GetGroupOccurrenceAsync(runId, opened.Value!.SnapshotToken, "absent-occurrence", Ct);

        missing.Status.Should().Be(WorkflowGroupReadStatus.OccurrenceNotFound);
    }

    [Fact]
    public async Task ChangedRunCreationStampResyncsAndEvictsSnapshot()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;
        proxy.StampOverrides[runId] = 2;

        var changed = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);
        var afterEviction = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);

        changed.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        afterEviction.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    [Fact]
    public async Task MissingRunCreationStampReturnsRunNotFoundAndEvictsSnapshot()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;
        proxy.Snapshots = new Dictionary<Guid, WorkflowGroupSnapshotData>();

        var missing = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);
        var afterEviction = await reader.QueryGroupDefinitionsAsync(runId, token, cancellationToken: Ct);

        missing.Status.Should().Be(WorkflowGroupReadStatus.RunNotFound);
        afterEviction.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    [Fact]
    public async Task DisposedReaderRejectsFurtherRequests()
    {
        var runId = Guid.NewGuid();
        var proxy = NewStore(Snapshot(runId));
        var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        reader.Dispose();

        var call = () => reader.GetRunGroupSnapshotAsync(runId, Ct).AsTask();

        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CrossRunAndMalformedCursorsRequireResynchronization()
    {
        var runId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        var proxy = NewStore(new Dictionary<Guid, WorkflowGroupSnapshotData>
        {
            [runId] = Snapshot(runId),
            [otherRunId] = Snapshot(otherRunId)
        });
        using var reader = Reader(proxy.Store, time: new FakeTimeProvider(Epoch));
        var opened = await reader.GetRunGroupSnapshotAsync(runId, Ct);
        var token = opened.Value!.SnapshotToken;

        var crossRun = await reader.QueryGroupDefinitionsAsync(otherRunId, token, cancellationToken: Ct);
        var badCursor = await reader.QueryGroupDefinitionsAsync(runId, token, cursor: "not-a-cursor", cancellationToken: Ct);

        crossRun.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        badCursor.Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    private static WorkflowGroupReader Reader(IWorkflowStore store, WorkflowGroupQueryOptions? options = null,
        FakeTimeProvider? time = null, IWorkflowGroupReadAuthorizer? authorizer = null) =>
        new(store, options, time, authorizer);

    private static WorkflowGroupDefinitionFact Definition(string key, string? parent, int ordinal, bool provenance = false) =>
        new(key, parent, provenance ? ActivityGroupKind.Macro : ActivityGroupKind.Logical, key, ordinal,
            provenance ? new WorkflowGroupProvenance("unknown-macro", "fingerprint", "invocation", null, null) : null, 0);

    private static WorkflowGroupSnapshotData Snapshot(Guid runId,
        IReadOnlyList<WorkflowGroupDefinitionFact>? definitions = null,
        IReadOnlyList<WorkflowGroupOccurrenceFact>? occurrences = null,
        long estimatedBytes = 128,
        IReadOnlyList<WorkflowGroupStepFact>? steps = null,
        IReadOnlyList<WorkflowGroupExpansionFact>? expansions = null,
        string workflowName = "reader-test",
        IReadOnlyList<WorkflowGroupChildRunFact>? childRuns = null) => new()
        {
            Run = new WorkflowGroupRunFact(runId, workflowName, "1", WorkflowStatus.Pending, null, null,
            Epoch, Epoch, null, 0, null, null, false),
            ThroughDurableSequence = 3,
            RunCreatedSequence = 1,
            EstimatedBytes = estimatedBytes,
            Definitions = definitions ?? [],
            Occurrences = occurrences ?? [],
            Steps = steps ?? [],
            Expansions = expansions ?? [],
            ChildRuns = childRuns ?? []
        };

    private static StoreProxy NewStore(WorkflowGroupSnapshotData data) => NewStore(new Dictionary<Guid, WorkflowGroupSnapshotData>
    {
        [data.Run.RunId] = data
    });

    private static StoreProxy NewStore(IReadOnlyDictionary<Guid, WorkflowGroupSnapshotData> snapshots)
    {
        var store = DispatchProxy.Create<IGroupReaderTestStore, StoreProxy>();
        var proxy = (StoreProxy)(object)store;
        proxy.Snapshots = snapshots;
        return proxy;
    }

    public interface IGroupReaderTestStore : IWorkflowStore, IWorkflowGroupSnapshotRepository { }

    public class StoreProxy : DispatchProxy
    {
        private int readCalls;
        public IReadOnlyDictionary<Guid, WorkflowGroupSnapshotData> Snapshots { get; set; } = new Dictionary<Guid, WorkflowGroupSnapshotData>();
        public Dictionary<Guid, long?> StampOverrides { get; } = [];
        public bool ReturnSuccessWithNullSnapshot { get; set; }
        public int ReadCalls => Volatile.Read(ref readCalls);
        public IWorkflowStore Store => (IWorkflowStore)(object)this;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var runId = args is { Length: > 0 } && args[0] is Guid id ? id : Guid.Empty;
            if (targetMethod.Name == nameof(IWorkflowGroupSnapshotRepository.ReadGroupSnapshotAsync))
            {
                Interlocked.Increment(ref readCalls);
                if (ReturnSuccessWithNullSnapshot)
                    return new ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>>(
                        new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.Success, null));
                var result = Snapshots.TryGetValue(runId, out var data)
                    ? new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.Success, data)
                    : new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.RunNotFound);
                return new ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>>(result);
            }
            if (targetMethod.Name == nameof(IWorkflowGroupSnapshotRepository.GetGroupSnapshotRunStampAsync))
                return new ValueTask<long?>(StampOverrides.TryGetValue(runId, out var stamp)
                    ? stamp
                    : Snapshots.TryGetValue(runId, out var data) ? data.RunCreatedSequence : null);
            throw new NotSupportedException($"Unexpected store call '{targetMethod.Name}'.");
        }
    }

    public class LegacyStoreProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new NotSupportedException($"Legacy store should not be called: '{targetMethod?.Name}'.");
        }
    }

    private sealed class CountingAuthorizer(WorkflowGroupReadAccess initialAccess) : IWorkflowGroupReadAuthorizer
    {
        public WorkflowGroupReadAccess Access { get; set; } = initialAccess;
        public int Calls { get; private set; }
        public ValueTask<WorkflowGroupReadAccess> AuthorizeAsync(Guid workflowRunId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(Access);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current += delta;
    }
}
