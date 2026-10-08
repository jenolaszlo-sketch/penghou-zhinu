using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Cli;

/// <summary>Machine-readable or human-readable rendering with payload redaction.</summary>
internal sealed class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool Json { get; }
    public bool IncludePayloads { get; }

    public CliOutput(bool json, bool includePayloads)
    {
        Json = json;
        IncludePayloads = includePayloads;
    }

    public string Payload(string? value) =>
        value is null ? "(null)" : IncludePayloads ? value : $"<hidden {value.Length} chars>";

    public string Render(object? value)
    {
        if (Json)
            return JsonSerializer.Serialize(value, JsonOptions);
        return value switch
        {
            IEnumerable<RunRow> rows => RenderTable(
                ["id", "name", "version", "status", "updated"],
                rows.Select(r => new[] { r.Id, r.Name, r.Version, r.Status, r.Updated })),
            RunDetail detail => RenderDetail(detail),
            IEnumerable<EventRow> events => RenderTable(
                ["seq", "type", "step", "at", "data"],
                events.Select(e => new[]
                {
                    e.Sequence, e.Type, e.Step, e.At,
                    e.Data.Length == 0 ? "-" : Payload(e.Data)
                })),
            IEnumerable<WaitRow> waits => RenderTable(
                ["step", "kind", "status", "signal", "deadline", "available"],
                waits.Select(w => new[] { w.Step, w.Kind, w.Status, w.Signal, w.Deadline, w.Available })),
            IEnumerable<ExternalOperationRow> operations => RenderTable(
                ["id", "step", "attempt", "provider", "status", "recovery", "updated"],
                operations.Select(o => new[]
                {
                    o.Id, o.Step, o.Attempt, o.Provider, o.Status, o.Recovery, o.Updated
                })),
            ExternalOperationDetail operation => RenderExternalOperation(operation),
            IEnumerable<PlanRow> plan => RenderTable(
                ["step", "reason"],
                plan.Select(p => new[] { p.Step, p.Reason })),
            RetentionSummary retention =>
                $"eligible: {retention.Eligible}\n" +
                string.Join("\n", retention.Sample.Select(id => $"  {id}")),
            SignalResult signal => $"buffered signal '{signal.Name}' for run {signal.RunId}",
            RunCancelResult cancel => $"{cancel.Action} {cancel.RunId} {cancel.Status}",
            RunWaitResult wait => $"{wait.Action} {wait.RunId} {wait.Status}",
            EvidenceDetail evidence => RenderEvidence(evidence),
            RestartOutcome restart => RenderRestart(restart),
            GraphDetail graph => RenderGraph(graph),
            string text => text,
            null => "(none)",
            _ => JsonSerializer.Serialize(value, JsonOptions)
        };
    }

    public object RunRows(IReadOnlyList<WorkflowRun> runs) =>
        runs.Select(run => new RunRow(
            run.Id.ToString("D"),
            run.WorkflowName,
            run.WorkflowVersion,
            run.Status.ToString(),
            run.UpdatedAt.ToString("O"))).ToList();

    public object RunDetailModel(WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowWait> waits) => new RunDetail(
        run.Id.ToString("D"),
        run.WorkflowName,
        run.WorkflowVersion,
        run.Status.ToString(),
        run.ParentRunId?.ToString("D") ?? "-",
        run.SourceRunId?.ToString("D") ?? "-",
        run.Deadline?.ToString("O") ?? "(none)",
        Payload(run.InputJson),
        Payload(run.OutputJson),
        run.Error?.Message ?? "(none)",
        steps.Select(step => new StepRow(
            step.StepKey,
            step.Revision.ToString(),
            step.Status.ToString(),
            step.Attempt.ToString())).ToList(),
        waits.Select(wait => new WaitRow(
            wait.StepKey,
            wait.Kind.ToString(),
            wait.Status.ToString(),
            wait.SignalName ?? "-",
            wait.DeadlineAt?.ToString("O") ?? "-",
            wait.AvailableAt?.ToString("O") ?? "-")).ToList());

    public object EventModels(IReadOnlyList<WorkflowEvent> events) =>
        events.Select(item => new EventRow(
            item.Sequence.ToString(),
            item.EventType,
            item.StepKey ?? "-",
            item.Timestamp.ToString("O"),
            item.DataJson ?? "")).ToList();

    public object WaitModels(IReadOnlyList<WorkflowWait> waits) =>
        waits.Select(wait => new WaitRow(
            wait.StepKey,
            wait.Kind.ToString(),
            wait.Status.ToString(),
            wait.SignalName ?? "-",
            wait.DeadlineAt?.ToString("O") ?? "-",
            wait.AvailableAt?.ToString("O") ?? "-")).ToList();

    public object ExternalOperationModels(IReadOnlyList<WorkflowExternalOperation> operations) =>
        operations.Select(operation => new ExternalOperationRow(
            operation.OperationId.ToString("D"),
            operation.StepKey ?? "-",
            operation.Attempt?.ToString() ?? "-",
            operation.Provider,
            operation.Status.ToString(),
            operation.RecoveryIntent.ToString(),
            operation.UpdatedAt.ToString("O"))).ToList();

    public object ExternalOperationModel(WorkflowExternalOperation operation) => new ExternalOperationDetail(
        operation.OperationId.ToString("D"),
        operation.WorkflowRunId.ToString("D"),
        operation.StepKey ?? "-",
        operation.StepRevision?.ToString() ?? "-",
        operation.Attempt?.ToString() ?? "-",
        operation.IdempotencyKey ?? "-",
        operation.Provider,
        operation.ExternalId ?? "-",
        operation.OwnerId ?? "-",
        operation.LeaseGeneration.ToString(),
        operation.Status.ToString(),
        operation.RecoveryIntent.ToString(),
        Payload(operation.PayloadJson),
        operation.Error ?? "-",
        operation.CreatedAt.ToString("O"),
        operation.UpdatedAt.ToString("O"),
        operation.CompletedAt?.ToString("O") ?? "-");

    public object PlanModels(RestartPlan plan) =>
        plan.StepsToInvalidate.Select(item => new PlanRow(
            item.StepKey, item.Reason.ToString())).ToList();

    public object ForkPlanModels(ForkPlan plan) =>
        new
        {
            reuse = plan.StepsToReuse,
            reexecute = plan.StepsToReexecute.Select(item => new PlanRow(
                item.StepKey, item.Reason.ToString())).ToList()
        };

    public object RestartOutcomeModel(Guid operationId, bool applied, RestartPlan plan) =>
        new RestartOutcome(
            operationId.ToString("D"),
            applied ? "applied" : "replayed",
            plan.StepsToInvalidate
                .Select(item => new PlanRow(item.StepKey, item.Reason.ToString()))
                .ToList());

    public object RetentionModel(RunRetentionPreview preview) => new RetentionSummary(
        preview.EligibleRunCount,
        preview.SampleRunIds.Select(id => id.ToString("D")).ToList());

    public object EvidenceModel(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowWait> waits,
        IReadOnlyList<WorkflowExternalOperation> operations,
        IReadOnlyList<WorkflowEvent> events) => new EvidenceDetail(
        run.Id.ToString("D"),
        run.WorkflowName,
        run.WorkflowVersion,
        run.Status.ToString(),
        run.DefinitionFingerprint ?? "-",
        run.Error?.Message ?? "(none)",
        Payload(run.OutputJson),
        steps
            .Where(step => step.Status != StepStatus.Completed)
            .Select(step => new EvidenceStep(
                step.StepKey,
                step.Revision.ToString(),
                step.Status.ToString(),
                step.Attempt.ToString(),
                step.Error?.Message ?? "-"))
            .ToList(),
        waits
            .Select(wait => new WaitRow(
                wait.StepKey,
                wait.Kind.ToString(),
                wait.Status.ToString(),
                wait.SignalName ?? "-",
                wait.DeadlineAt?.ToString("O") ?? "-",
                wait.AvailableAt?.ToString("O") ?? "-"))
            .ToList(),
        operations
            .Where(operation => operation.Status != ExternalOperationStatus.Completed)
            .Select(operation => new EvidenceOperation(
                operation.OperationId.ToString("D"),
                operation.StepKey ?? "-",
                operation.Status.ToString(),
                operation.Provider,
                EvidenceOperationDetail(operation)))
            .ToList(),
        events.Count,
        events
            .Where(item => item.EventType is WorkflowEventTypes.WorkflowCancelled
                or WorkflowEventTypes.StepRestarted
                or WorkflowEventTypes.RunForked
                or WorkflowEventTypes.WorkflowFailed
                or WorkflowEventTypes.StepFailed)
            .Select(item => new EvidenceAudit(
                item.EventType,
                item.Timestamp.ToString("O"),
                Payload(item.DataJson)))
            .ToList());

    public object GraphModel(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowWait> waits,
        IReadOnlyList<StepDependency> dependencies) => new GraphDetail(
        run.Id.ToString("D"),
        run.WorkflowName,
        run.WorkflowVersion,
        run.Status.ToString(),
        run.DefinitionFingerprint ?? "(not recorded)",
        steps
            .OrderBy(step => step.StepKey, StringComparer.Ordinal)
            .Select(step => new GraphNode(
                step.StepKey,
                step.Revision.ToString(),
                step.Status.ToString(),
                step.Attempt.ToString(),
                Lease(step),
                step.SignalName ?? "-"))
            .ToList(),
        dependencies
            .OrderBy(edge => edge.StepKey, StringComparer.Ordinal)
            .ThenBy(edge => edge.DependsOnStepKey, StringComparer.Ordinal)
            .Select(edge => new GraphEdge(edge.StepKey, edge.DependsOnStepKey))
            .ToList(),
        waits.Select(wait => new WaitRow(
            wait.StepKey,
            wait.Kind.ToString(),
            wait.Status.ToString(),
            wait.SignalName ?? "-",
            wait.DeadlineAt?.ToString("O") ?? "-",
            wait.AvailableAt?.ToString("O") ?? "-")).ToList());

    // Read-only summary of a step's current lease. A leased row is evidence, not a
    // claim: a step with no owner is unleased, and an elapsed expiry is marked so a
    // stale row is never read as active.
    private static string Lease(WorkflowStepRun step)
    {
        if (step.LeaseOwner is null)
            return "-";
        if (step.LeaseExpiresAt is not { } expires)
            return $"{step.LeaseOwner} (expiry not recorded)";
        var expired = expires <= DateTimeOffset.UtcNow ? " (expired)" : "";
        return $"{step.LeaseOwner} until {expires:O}{expired}";
    }

    private static string RenderTable(string[] headers, IEnumerable<string[]> rows)
    {
        var lines = new List<string> { string.Join("  ", headers) };
        lines.AddRange(rows.Select(cells => string.Join("  ", cells)));
        return string.Join("\n", lines);
    }

    private static string RenderRestart(RestartOutcome restart)
    {
        var lines = new List<string>
        {
            $"restart {restart.OperationId} {restart.Disposition} ({restart.Steps.Count} invalidated)"
        };
        lines.AddRange(restart.Steps.Select(step => $"  {step.Step} {step.Reason}"));
        return string.Join("\n", lines);
    }

    private static string RenderGraph(GraphDetail graph)
    {
        var lines = new List<string>
        {
            $"run: {graph.Id}",
            $"workflow: {graph.Name} version {graph.Version}",
            $"status: {graph.Status}",
            $"fingerprint: {graph.Fingerprint}",
            $"steps ({graph.Nodes.Count}):"
        };
        if (graph.Nodes.Count == 0)
            lines.Add("  (none recorded)");
        else
            lines.AddRange(graph.Nodes.Select(node =>
                $"  {node.Key} rev {node.Revision} {node.Status} attempt {node.Attempt} " +
                $"lease {node.Lease} signal={node.Signal}"));
        lines.Add($"edges ({graph.Edges.Count}):");
        if (graph.Edges.Count == 0)
            lines.Add("  (none recorded)");
        else
            lines.AddRange(graph.Edges.Select(edge => $"  {edge.Step} -> {edge.DependsOn}"));
        lines.Add($"waits ({graph.Waits.Count}):");
        if (graph.Waits.Count == 0)
            lines.Add("  (none recorded)");
        else
            lines.AddRange(graph.Waits.Select(wait =>
                $"  {wait.Step} {wait.Kind} {wait.Status} signal={wait.Signal} deadline={wait.Deadline}"));
        return string.Join("\n", lines);
    }

    private string RenderEvidence(EvidenceDetail evidence)
    {
        var lines = new List<string>
        {
            $"run: {evidence.Id} {evidence.Name} version {evidence.Version} {evidence.Status}",
            $"fingerprint: {evidence.Fingerprint}",
            $"error: {evidence.Error}",
            $"output: {evidence.Output}",
            $"attention steps ({evidence.Steps.Count}):"
        };
        lines.AddRange(evidence.Steps.Select(step =>
            $"  {step.Key} rev {step.Revision} {step.Status} attempt {step.Attempt} error: {step.Error}"));
        lines.Add($"waits ({evidence.Waits.Count}):");
        lines.AddRange(evidence.Waits.Select(wait =>
            $"  {wait.Step} {wait.Kind} {wait.Status} signal={wait.Signal} deadline={wait.Deadline}"));
        lines.Add($"external operations ({evidence.Operations.Count}):");
        lines.AddRange(evidence.Operations.Select(operation =>
            $"  {operation.Id} {operation.Step} {operation.Status} {operation.Provider} {operation.Detail}"));
        lines.Add($"audit ({evidence.Audit.Count} of {evidence.EventCount} events):");
        lines.AddRange(evidence.Audit.Select(item => $"  {item.Type} {item.At} {item.Data}"));
        return string.Join("\n", lines);
    }

    private string EvidenceOperationDetail(WorkflowExternalOperation operation) =>
        operation.Status switch
        {
            ExternalOperationStatus.Failed =>
                "error: " + Truncate(operation.Error ?? "-", 300),
            ExternalOperationStatus.Cancelled =>
                "reason: " + Truncate(operation.Error ?? "-", 300) +
                " payload: " + Payload(operation.PayloadJson),
            _ => "recovery: " + operation.RecoveryIntent +
                " payload: " + Payload(operation.PayloadJson),
        };

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "...";

    private string RenderExternalOperation(ExternalOperationDetail operation) =>
        string.Join("\n", new[]
        {
            $"operation: {operation.Id}",
            $"run: {operation.RunId}",
            $"step: {operation.Step} revision {operation.StepRevision} attempt {operation.Attempt}",
            $"idempotency: {operation.IdempotencyKey}",
            $"provider: {operation.Provider} external {operation.ExternalId}",
            $"owner: {operation.Owner} generation {operation.LeaseGeneration}",
            $"status: {operation.Status} recovery {operation.RecoveryIntent}",
            $"payload: {operation.Payload}",
            $"error: {operation.Error}",
            $"created: {operation.Created} updated: {operation.Updated} completed: {operation.Completed}"
        });

    private string RenderDetail(RunDetail detail)
    {
        var lines = new List<string>
        {
            $"run: {detail.Id}",
            $"workflow: {detail.Name} version {detail.Version}",
            $"status: {detail.Status}",
            $"parent: {detail.Parent}",
            $"source: {detail.Source}",
            $"deadline: {detail.Deadline}",
            $"input: {detail.Input}",
            $"output: {detail.Output}",
            $"error: {detail.Error}",
            "steps:"
        };
        lines.AddRange(detail.Steps.Select(step =>
            $"  {step.Key} rev {step.Revision} {step.Status} attempt {step.Attempt}"));
        lines.Add("waits:");
        lines.AddRange(detail.Waits.Select(wait =>
            $"  {wait.Step} {wait.Kind} {wait.Status} signal={wait.Signal} deadline={wait.Deadline}"));
        return string.Join("\n", lines);
    }

    internal sealed record RunRow(string Id, string Name, string Version, string Status, string Updated);
    internal sealed record StepRow(string Key, string Revision, string Status, string Attempt);
    internal sealed record WaitRow(
        string Step, string Kind, string Status, string Signal, string Deadline, string Available);
    internal sealed record EventRow(string Sequence, string Type, string Step, string At, string Data);
    internal sealed record ExternalOperationRow(
        string Id, string Step, string Attempt, string Provider, string Status, string Recovery, string Updated);
    internal sealed record ExternalOperationDetail(
        string Id, string RunId, string Step, string StepRevision, string Attempt, string IdempotencyKey,
        string Provider, string ExternalId, string Owner, string LeaseGeneration, string Status,
        string RecoveryIntent, string Payload, string Error, string Created, string Updated, string Completed);
    internal sealed record PlanRow(string Step, string Reason);
    internal sealed record RetentionSummary(int Eligible, List<string> Sample);
    internal sealed record SignalResult(string RunId, string Name);
    internal sealed record RunCancelResult(string Action, string RunId, string Status);
    internal sealed record RunWaitResult(string Action, string RunId, string Status);
    internal sealed record RestartOutcome(string OperationId, string Disposition, List<PlanRow> Steps);
    internal sealed record EvidenceStep(string Key, string Revision, string Status, string Attempt, string Error);
    internal sealed record EvidenceOperation(string Id, string Step, string Status, string Provider, string Detail);
    internal sealed record EvidenceAudit(string Type, string At, string Data);
    internal sealed record EvidenceDetail(
        string Id, string Name, string Version, string Status, string Fingerprint, string Error, string Output,
        List<EvidenceStep> Steps, List<WaitRow> Waits, List<EvidenceOperation> Operations,
        int EventCount, List<EvidenceAudit> Audit);
    internal sealed record GraphNode(
        string Key, string Revision, string Status, string Attempt, string Lease, string Signal);
    internal sealed record GraphEdge(string Step, string DependsOn);
    internal sealed record GraphDetail(
        string Id, string Name, string Version, string Status, string Fingerprint,
        List<GraphNode> Nodes, List<GraphEdge> Edges, List<WaitRow> Waits);
    internal sealed record RunDetail(
        string Id, string Name, string Version, string Status,
        string Parent, string Source, string Deadline,
        string Input, string Output, string Error,
        List<StepRow> Steps, List<WaitRow> Waits);
}
