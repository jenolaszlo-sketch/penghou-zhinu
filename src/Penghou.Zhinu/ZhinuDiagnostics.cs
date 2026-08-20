using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Penghou.Zhinu;

/// <summary>Stable diagnostics conventions for tracing and metrics listeners.</summary>
public static class ZhinuDiagnostics
{
    public const string ActivitySourceName = "Penghou.Zhinu";
    public const string MeterName = "Penghou.Zhinu";

    public static class Activities
    {
        public const string WorkflowExecute = "zhinu.workflow.execute";
        public const string StepExecute = "zhinu.step.execute";
        public const string ChildExecute = "zhinu.child.execute";
        public const string SignalWait = "zhinu.signal.wait";
        public const string DelayWait = "zhinu.delay.wait";
        public const string CompensationExecute = "zhinu.compensation.execute";
        public const string RollbackExecute = "zhinu.rollback.execute";
        public const string OperationExecute = "zhinu.operation.execute";
        public const string LeaseRecover = "zhinu.lease.recover";
        public const string ArtifactPublish = "zhinu.artifact.publish";
    }

    public static class Attributes
    {
        public const string WorkflowRunId = "zhinu.workflow.run.id";
        public const string WorkflowName = "zhinu.workflow.name";
        public const string WorkflowVersion = "zhinu.workflow.version";
        public const string WorkflowStatus = "zhinu.workflow.status";
        public const string ParentRunId = "zhinu.workflow.parent_run.id";
        public const string SourceRunId = "zhinu.workflow.source_run.id";
        public const string StepId = "zhinu.step.id";
        public const string StepKey = "zhinu.step.key";
        public const string StepAttempt = "zhinu.step.attempt";
        public const string StepRevision = "zhinu.step.revision";
        public const string LeaseGeneration = "zhinu.lease.generation";
        public const string OperationId = "zhinu.operation.id";
        public const string OperationType = "zhinu.operation.type";
        public const string ExecutionDisposition = "zhinu.execution.disposition";
        public const string RetryScheduled = "zhinu.retry.scheduled";
        public const string ErrorType = "error.type";
        public const string ArtifactId = "zhinu.artifact.id";
        public const string ArtifactName = "zhinu.artifact.name";
        public const string ArtifactType = "zhinu.artifact.type";
        public const string ArtifactRevision = "zhinu.artifact.revision";
        public const string ArtifactCreated = "zhinu.artifact.created";
    }

    public static class Metrics
    {
        public const string RunsStarted = "zhinu.runs.started";
        public const string RunsCompleted = "zhinu.runs.completed";
        public const string RunsFailed = "zhinu.runs.failed";
        public const string RunsCancelled = "zhinu.runs.cancelled";
        public const string RunsActive = "zhinu.runs.active";
        public const string RunDuration = "zhinu.run.duration";
        public const string StepsExecuted = "zhinu.steps.executed";
        public const string StepsReused = "zhinu.steps.reused";
        public const string StepsFailed = "zhinu.steps.failed";
        public const string StepsRetried = "zhinu.steps.retried";
        public const string StepDuration = "zhinu.step.duration";
        public const string SignalsDelivered = "zhinu.signals.delivered";
        public const string CompensationsExecuted = "zhinu.compensations.executed";
        public const string RollbacksCompleted = "zhinu.rollbacks.completed";
        public const string LeasesRecovered = "zhinu.leases.recovered";
        public const string ArtifactsPublished = "zhinu.artifacts.published";
    }

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> RunsStartedCounter = Meter.CreateCounter<long>(Metrics.RunsStarted);
    internal static readonly Counter<long> RunsCompletedCounter = Meter.CreateCounter<long>(Metrics.RunsCompleted);
    internal static readonly Counter<long> RunsFailedCounter = Meter.CreateCounter<long>(Metrics.RunsFailed);
    internal static readonly Counter<long> RunsCancelledCounter = Meter.CreateCounter<long>(Metrics.RunsCancelled);
    internal static readonly UpDownCounter<long> RunsActiveCounter = Meter.CreateUpDownCounter<long>(Metrics.RunsActive);
    internal static readonly Histogram<double> RunDurationHistogram = Meter.CreateHistogram<double>(Metrics.RunDuration, "s");
    internal static readonly Counter<long> StepsExecutedCounter = Meter.CreateCounter<long>(Metrics.StepsExecuted);
    internal static readonly Counter<long> StepsReusedCounter = Meter.CreateCounter<long>(Metrics.StepsReused);
    internal static readonly Counter<long> StepsFailedCounter = Meter.CreateCounter<long>(Metrics.StepsFailed);
    internal static readonly Counter<long> StepsRetriedCounter = Meter.CreateCounter<long>(Metrics.StepsRetried);
    internal static readonly Histogram<double> StepDurationHistogram = Meter.CreateHistogram<double>(Metrics.StepDuration, "s");
    internal static readonly Counter<long> SignalsDeliveredCounter = Meter.CreateCounter<long>(Metrics.SignalsDelivered);
    internal static readonly Counter<long> CompensationsExecutedCounter = Meter.CreateCounter<long>(Metrics.CompensationsExecuted);
    internal static readonly Counter<long> RollbacksCompletedCounter = Meter.CreateCounter<long>(Metrics.RollbacksCompleted);
    internal static readonly Counter<long> LeasesRecoveredCounter = Meter.CreateCounter<long>(Metrics.LeasesRecovered);
    internal static readonly Counter<long> ArtifactsPublishedCounter = Meter.CreateCounter<long>(Metrics.ArtifactsPublished);

    internal static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        ActivitySource.StartActivity(name, kind);

    internal static Activity? StartWorkflowActivity(WorkflowRun? run)
    {
        var ambient = Activity.Current?.Context;
        Activity? activity;
        if (TryParseTraceId(run?.TraceId, out var traceId))
        {
            var parent = new ActivityContext(
                traceId,
                ActivitySpanId.CreateRandom(),
                ActivityTraceFlags.Recorded);
            var links = ambient is { } context && context.TraceId != traceId
                ? new[] { new ActivityLink(context) }
                : null;
            activity = ActivitySource.StartActivity(
                Activities.WorkflowExecute,
                ActivityKind.Internal,
                parent,
                links: links);
        }
        else
        {
            activity = StartActivity(Activities.WorkflowExecute);
        }
        activity?.SetTag(Attributes.WorkflowRunId, run?.Id);
        activity?.SetTag(Attributes.WorkflowName, run?.WorkflowName);
        activity?.SetTag(Attributes.WorkflowVersion, run?.WorkflowVersion);
        activity?.SetTag(Attributes.ParentRunId, run?.ParentRunId);
        activity?.SetTag(Attributes.SourceRunId, run?.SourceRunId);
        activity?.SetTag(Attributes.LeaseGeneration, run?.LeaseGeneration);
        return activity;
    }

    private static bool TryParseTraceId(
        string? value,
        out ActivityTraceId traceId)
    {
        traceId = default;
        if (value is not { Length: 32 } || value.Any(character => !Uri.IsHexDigit(character)))
            return false;
        traceId = ActivityTraceId.CreateFromString(value.AsSpan());
        return traceId != default;
    }

    internal static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
            return;
        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(Attributes.ErrorType, exception.GetType().FullName);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName
        }));
    }
}
