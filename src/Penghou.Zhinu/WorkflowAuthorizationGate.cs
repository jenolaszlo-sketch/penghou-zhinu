using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu;

// Versioned runtime wire/digest profile. Never uses caller serializer settings or record hashes.
internal static class WorkflowAuthorizationCodec
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static string Binding(WorkflowExecutionAuthorizationOptions options) => Hash(new
    {
        Version = 1,
        options.ProviderId,
        options.BindingId,
        RequiresEvidence = options.EvidenceVerifier is not null,
        EvaluationTimeoutTicks = options.EvaluationTimeout.Ticks,
        MaximumDecisionLifetimeTicks = options.MaximumDecisionLifetime.Ticks,
        MaximumClockSkewTicks = options.MaximumClockSkew.Ticks
    });

    internal static string Declaration(WorkflowAuthorizationDeclaration? forward,
        WorkflowAuthorizationDeclaration? compensation) => Hash(new { Version = 1, Forward = forward, Compensation = compensation });

    internal static string Context(string binding, ExecutionAuthorizationContext context) =>
        Hash(new { Version = 1, Binding = binding, Context = context });

    internal static string SerializeDeclaration(WorkflowAuthorizationDeclaration? value) =>
        JsonSerializer.Serialize(value ?? new WorkflowAuthorizationDeclaration([]), Json);

    internal static WorkflowAuthorizationDeclaration ReadDeclaration(string json) =>
        JsonSerializer.Deserialize<WorkflowAuthorizationDeclaration>(json, Json) ??
        throw new WorkflowAuthorizationException("Missing durable compensation declaration.");

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
}

internal sealed class WorkflowAuthorizationGate(
    IWorkflowStore store, ZhinuOptions options, TimeProvider timeProvider, string ownerId)
{
    internal static void ValidateBinding(WorkflowRun run, ZhinuOptions options)
    {
        var configured = options.ExecutionAuthorization;
        if (!string.Equals(run.AuthorizationProviderId, configured?.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(run.AuthorizationBindingId,
                configured is null ? null : WorkflowAuthorizationCodec.Binding(configured), StringComparison.Ordinal))
            throw new WorkflowAuthorizationException("The run's retained authority binding does not match this host.");
    }

    internal async Task AuthorizeAsync(Guid runId, Guid claimId, string stepKey, int revision,
        long generation, int attempt, bool compensation, string declarationHash,
        WorkflowAuthorizationDeclaration? declaration, CancellationToken cancellationToken)
    {
        var configured = options.ExecutionAuthorization;
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false) ??
            throw new WorkflowAuthorizationException("Authorization run was removed.");
        ValidateBinding(run, options);
        if (configured is null) return; // Explicit historical unprotected profile, never a configured-provider fallback.
        var repository = (IWorkflowAuthorizationRepository)store;
        var binding = WorkflowAuthorizationCodec.Binding(configured);
        var pending = await repository.GetPendingAuthorizationAsync(runId, claimId, compensation, cancellationToken)
            .ConfigureAwait(false);
        if (pending is { Ready: false })
        {
            if (pending.DeclarationHash != declarationHash || pending.BindingId != binding || pending.Revision != revision)
                throw new WorkflowAuthorizationException("Pending approval declaration changed.");
            throw new ParkedExecutionException(claimId);
        }
        var identity = new ExecutionIdentity(runId.ToString("N"),
            (compensation ? "compensation:" : "step:") + claimId.ToString("N"), attempt,
            $"g{generation}:r{revision}", run.ParentRunId?.ToString("N"), stepKey,
            declaration?.PlanId, declaration?.PlanRevision);
        var context = new ExecutionAuthorizationContext(identity, Guid.NewGuid().ToString("N"), declaration?.Requirements ?? []);
        ExecutionAuthorizationResult result;
        using var evaluationTimeout = new CancellationTokenSource(configured.EvaluationTimeout, timeProvider);
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, evaluationTimeout.Token);
        try
        {
            result = await configured.Authorizer.AuthorizeAsync(context, evaluationCancellation.Token).AsTask()
                .WaitAsync(configured.EvaluationTimeout, timeProvider, evaluationCancellation.Token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (result is null || result.AuthorizationRequestId != context.AuthorizationRequestId ||
                result.ProviderId != configured.ProviderId || result.EvaluatedAt > now + configured.MaximumClockSkew ||
                (result.Decision == ExecutionAuthorizationDecision.Allowed &&
                 (result.ExpiresAt <= now || result.ExpiresAt - result.EvaluatedAt > configured.MaximumDecisionLifetime)))
                result = Error(context, configured, "InvalidProviderResponse");
            else if (configured.EvidenceVerifier is { } verifier &&
                     (result.EvidenceId is null || !await verifier.VerifyAsync(context, result, evaluationCancellation.Token).AsTask()
                         .WaitAsync(configured.EvaluationTimeout, timeProvider, evaluationCancellation.Token).ConfigureAwait(false)))
                result = Error(context, configured, "EvidenceUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { result = Error(context, configured, "ProviderTimeout", ExecutionAuthorizationDecision.Unavailable); }
        catch (OperationCanceledException)
        {
            result = Error(context, configured, evaluationTimeout.IsCancellationRequested ? "ProviderTimeout" : "ProviderCancelled",
                ExecutionAuthorizationDecision.Unavailable);
        }
        catch (Exception) { result = Error(context, configured, "ProviderError"); }
        cancellationToken.ThrowIfCancellationRequested();
        var committedAt = timeProvider.GetUtcNow();
        if (result.Decision == ExecutionAuthorizationDecision.Allowed && result.ExpiresAt <= committedAt)
            result = Error(context, configured, "ExpiredDecision");
        var commit = new WorkflowAuthorizationCommit
        {
            WorkflowRunId = runId,
            ClaimId = claimId,
            StepKey = stepKey,
            Revision = revision,
            LeaseGeneration = generation,
            OwnerId = ownerId,
            IsCompensation = compensation,
            BindingId = binding,
            DeclarationHash = declarationHash,
            ContextHash = WorkflowAuthorizationCodec.Context(binding, context),
            Context = context,
            Result = result,
            Now = committedAt
        };
        await repository.CommitAuthorizationAsync(commit, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Decision == ExecutionAuthorizationDecision.ApprovalRequired)
            throw new ParkedExecutionException(claimId);
        if (result.Decision != ExecutionAuthorizationDecision.Allowed)
            throw new WorkflowAuthorizationException($"Authorization ended with {result.Decision}.");
        if (result.ExpiresAt <= timeProvider.GetUtcNow() ||
            !await repository.ValidateAuthorizationDispatchAsync(commit, cancellationToken).ConfigureAwait(false))
            throw new WorkflowAuthorizationException("Authorization expired or its dispatch fence was lost.");
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExpiresAt <= timeProvider.GetUtcNow())
            throw new WorkflowAuthorizationException("Authorization expired during dispatch validation.");
    }

    private ExecutionAuthorizationResult Error(ExecutionAuthorizationContext context,
        WorkflowExecutionAuthorizationOptions configured, string reason,
        ExecutionAuthorizationDecision decision = ExecutionAuthorizationDecision.Error) =>
        new(decision, context.AuthorizationRequestId, configured.ProviderId, Guid.NewGuid().ToString("N"),
            timeProvider.GetUtcNow(), reasonCode: reason);
}
