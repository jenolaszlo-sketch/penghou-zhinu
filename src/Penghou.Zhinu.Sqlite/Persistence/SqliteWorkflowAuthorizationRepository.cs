using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Workflow.Abstractions;
using Penghou.Zhinu.Sqlite.Persistence;

namespace Penghou.Zhinu.Sqlite.Persistence;

/// <summary>Persists authorization checkpoints and dispatch fences in the workflow database.</summary>
internal sealed class SqliteWorkflowAuthorizationRepository(IZhinuSqliteDatabase factory)
{
    private static readonly JsonSerializerOptions JsonOptions = WorkflowAuthorizationCodec.Json;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public async ValueTask<WorkflowAuthorizationPending?> GetPendingAuthorizationAsync(
        Guid workflowRunId, Guid claimId, bool isCompensation, CancellationToken cancellationToken)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT step_key, revision, lease_generation, binding_id, declaration_hash,
                   context_hash, context_json, result_json, ready,
                   CASE WHEN is_compensation=0 THEN
                     (SELECT s.authorization_declaration_hash FROM workflow_steps s WHERE s.id=claim_id)
                   ELSE (SELECT c.authorization_declaration_json FROM workflow_step_compensations c WHERE c.id=claim_id) END,
                   provider_id
            FROM workflow_authorization_checkpoints p
            WHERE workflow_run_id=$run AND claim_id=$claim AND is_compensation=$comp
              AND decision=$approval
              AND EXISTS (SELECT 1 FROM workflow_runs r WHERE r.id=p.workflow_run_id
                AND r.authorization_provider_id=p.provider_id AND r.authorization_binding_id=p.binding_id
                AND (p.ready=1 OR p.lease_generation=r.lease_generation))
              AND ((is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s WHERE s.id=p.claim_id
                    AND s.revision=p.revision AND s.authorization_declaration_hash=p.declaration_hash
                    AND s.status IN (1,4)))
                OR (is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c
                    WHERE c.id=p.claim_id AND c.revision=p.revision AND c.status IN (0,1))));
            """);
        BindKey(command, workflowRunId, claimId, isCompensation);
        command.Parameters.AddWithValue("$approval", (int)ExecutionAuthorizationDecision.ApprovalRequired);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            var durableDeclaration = SqliteStoreSupport.GetNullableString(reader, 9);
            var expectedHash = isCompensation && durableDeclaration is not null
                ? WorkflowAuthorizationCodec.Declaration(null, WorkflowAuthorizationCodec.ReadDeclaration(durableDeclaration))
                : durableDeclaration;
            if (!string.Equals(expectedHash, reader.GetString(4), StringComparison.Ordinal)) return null;
            var bindingId = reader.GetString(3);
            var declarationHash = reader.GetString(4);
            var contextHash = reader.GetString(5);
            var context = JsonSerializer.Deserialize<ExecutionAuthorizationContext>(reader.GetString(6), JsonOptions)
                ?? throw new JsonException("Pending authorization context was null.");
            var result = JsonSerializer.Deserialize<ExecutionAuthorizationResult>(reader.GetString(7), JsonOptions)
                ?? throw new JsonException("Pending authorization result was null.");
            var revision = reader.GetInt32(1);
            var generation = reader.GetInt64(2);
            var stepKey = reader.GetString(0);
            var expectedOperation = (isCompensation ? "compensation:" : "step:") + claimId.ToString("N");
            if (context.AuthorizationRequestId != result.AuthorizationRequestId ||
                result.Decision != ExecutionAuthorizationDecision.ApprovalRequired ||
                result.ApprovalRequestId is null || result.ProviderId != reader.GetString(10) ||
                context.Identity.ExecutionId != workflowRunId.ToString("N") ||
                context.Identity.OperationId != expectedOperation ||
                context.Identity.OperationPath != stepKey ||
                context.Identity.ExecutionRevision != $"g{generation}:r{revision}" ||
                !IsHash(contextHash) || contextHash != WorkflowAuthorizationCodec.Context(bindingId, context))
                throw new JsonException("Pending authorization correlation or snapshot digest does not match.");
            return new WorkflowAuthorizationPending
            {
                WorkflowRunId = workflowRunId,
                ClaimId = claimId,
                IsCompensation = isCompensation,
                StepKey = stepKey,
                Revision = revision,
                LeaseGeneration = generation,
                BindingId = bindingId,
                DeclarationHash = declarationHash,
                ContextHash = contextHash,
                Context = context,
                Result = result,
                Ready = reader.GetInt32(8) != 0
            };
        }
        catch (JsonException exception)
        {
            throw new WorkflowAuthorizationException($"Persisted authorization checkpoint is malformed: {exception.Message}");
        }
    }

    public async ValueTask CommitAuthorizationAsync(WorkflowAuthorizationCommit request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = factory.TimeProvider.GetUtcNow();
        var run = await ReadRunAsync(connection, transaction, request.WorkflowRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowAuthorizationException("Workflow run no longer exists.");
        if (run.AuthorizationProviderId is null || run.AuthorizationBindingId is null ||
            run.AuthorizationBindingId != request.BindingId)
            throw new WorkflowAuthorizationException("The workflow's protected provider binding does not match this authorization request.");
        var allowedRunStatus = request.IsCompensation
            ? run.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.RollingBack
            : run.Status is WorkflowStatus.Pending or WorkflowStatus.Running;
        if (!allowedRunStatus || run.LeaseOwner != request.OwnerId || run.LeaseExpiresAt is not { } runExpiry || runExpiry <= now ||
            run.LeaseGeneration != request.LeaseGeneration)
            throw new WorkflowAuthorizationException("Workflow run lease or generation was lost before authorization could be committed.");
        await ValidateActiveGenerationAsync(connection, transaction, request.WorkflowRunId, request.IsCompensation, cancellationToken).ConfigureAwait(false);
        var attempt = request.Context.Identity.Attempt;
        var row = await ReadClaimAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Value.Owner != request.OwnerId || row.Value.LeaseExpiry <= now ||
            row.Value.Generation != request.LeaseGeneration || row.Value.Revision != request.Revision ||
            row.Value.Attempt != attempt ||
            row.Value.StepKey != request.StepKey || row.Value.Hash != request.DeclarationHash ||
            row.Value.Status != (request.IsCompensation ? (int)CompensationStatus.Running : (int)StepStatus.Running))
            throw new WorkflowAuthorizationException("The exact step or compensation claim is no longer current and leased.");
        var expectedOperation = (request.IsCompensation ? "compensation:" : "step:") + request.ClaimId.ToString("N");
        var expectedRevision = $"g{request.LeaseGeneration}:r{request.Revision}";
        if (request.Context.Identity.ExecutionId != request.WorkflowRunId.ToString("N") ||
            request.Context.Identity.OperationId != expectedOperation ||
            request.Context.Identity.ExecutionRevision != expectedRevision ||
            request.Context.Identity.ParentExecutionId != run.ParentRunId?.ToString("N") ||
            request.Context.Identity.OperationPath != request.StepKey ||
            attempt < 1 || !IsBoundedId(request.Context.AuthorizationRequestId) ||
            !IsBoundedId(request.Result.AuthorizationRequestId) ||
            !IsHash(request.ContextHash) || !IsHash(request.DeclarationHash))
            throw new WorkflowAuthorizationException("Authorization context identity or digest is invalid.");
        if (request.ContextHash != WorkflowAuthorizationCodec.Context(request.BindingId, request.Context) ||
            request.Result.AuthorizationRequestId != request.Context.AuthorizationRequestId ||
            request.Result.ProviderId != run.AuthorizationProviderId)
            throw new WorkflowAuthorizationException("Authorization result or context digest does not match the retained request.");
        if (request.Result.Decision == ExecutionAuthorizationDecision.Allowed &&
            (request.Result.ExpiresAt is not { } allowedExpiry || allowedExpiry <= now))
            throw new WorkflowAuthorizationException("Allowed authorization expired while the commit was waiting for storage.");
        var expectedAttempt = await GetNextAttemptAsync(connection, transaction, request.WorkflowRunId,
            request.ClaimId, request.IsCompensation, cancellationToken).ConfigureAwait(false);
        if (attempt != expectedAttempt)
            throw new WorkflowAuthorizationException("Authorization attempt does not match the persisted dispatch-start counter.");

        var identity = request.Context.Identity;
        var providerId = run.AuthorizationProviderId;
        var contextJson = JsonSerializer.Serialize(request.Context, JsonOptions);
        var resultJson = JsonSerializer.Serialize(request.Result, JsonOptions);
        if (Encoding.UTF8.GetByteCount(contextJson) > 2_097_152 || Encoding.UTF8.GetByteCount(resultJson) > 32_768)
            throw new WorkflowAuthorizationException("Authorization evidence exceeds the persisted size limit.");
        await using (var evidence = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_authorization_evidence
            (workflow_run_id,claim_id,is_compensation,step_key,revision,lease_generation,attempt,
             provider_id,binding_id,declaration_hash,context_hash,authorization_request_id,
             decision,context_json,result_json,committed_at)
            VALUES ($run,$claim,$comp,$step,$revision,$generation,$attempt,$provider,$binding,
             $declaration,$contextHash,$requestId,$decision,$context,$result,$now);
            """))
        {
            BindKey(evidence, request.WorkflowRunId, request.ClaimId, request.IsCompensation);
            evidence.Parameters.AddWithValue("$step", request.StepKey);
            evidence.Parameters.AddWithValue("$revision", request.Revision);
            evidence.Parameters.AddWithValue("$generation", request.LeaseGeneration);
            evidence.Parameters.AddWithValue("$attempt", attempt);
            evidence.Parameters.AddWithValue("$provider", providerId);
            evidence.Parameters.AddWithValue("$binding", request.BindingId);
            evidence.Parameters.AddWithValue("$declaration", request.DeclarationHash);
            evidence.Parameters.AddWithValue("$contextHash", request.ContextHash);
            evidence.Parameters.AddWithValue("$requestId", request.Result.AuthorizationRequestId);
            evidence.Parameters.AddWithValue("$decision", (int)request.Result.Decision);
            evidence.Parameters.AddWithValue("$context", contextJson);
            evidence.Parameters.AddWithValue("$result", resultJson);
            evidence.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
            await evidence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = SqliteStoreSupport.CreateCommand(connection, transaction, """
            DELETE FROM workflow_authorization_checkpoints
            WHERE workflow_run_id=$run AND claim_id=$claim AND is_compensation=$comp;
            """))
        {
            BindKey(delete, request.WorkflowRunId, request.ClaimId, request.IsCompensation);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (request.Result.Decision == ExecutionAuthorizationDecision.ApprovalRequired)
        {
            await using var park = SqliteStoreSupport.CreateCommand(connection, transaction, """
                INSERT INTO workflow_authorization_checkpoints
                (workflow_run_id,claim_id,is_compensation,step_key,revision,lease_generation,
                 provider_id,binding_id,declaration_hash,context_hash,authorization_request_id,
                 approval_request_id,decision,ready,context_json,result_json,created_at)
                VALUES ($run,$claim,$comp,$step,$revision,$generation,$provider,$binding,
                 $declaration,$contextHash,$requestId,$approval,$decision,0,$context,$result,$now);
                """);
            BindKey(park, request.WorkflowRunId, request.ClaimId, request.IsCompensation);
            park.Parameters.AddWithValue("$step", request.StepKey);
            park.Parameters.AddWithValue("$revision", request.Revision);
            park.Parameters.AddWithValue("$generation", request.LeaseGeneration);
            park.Parameters.AddWithValue("$provider", providerId);
            park.Parameters.AddWithValue("$binding", request.BindingId);
            park.Parameters.AddWithValue("$declaration", request.DeclarationHash);
            park.Parameters.AddWithValue("$contextHash", request.ContextHash);
            park.Parameters.AddWithValue("$requestId", request.Result.AuthorizationRequestId);
            park.Parameters.AddWithValue("$approval", request.Result.ApprovalRequestId!);
            park.Parameters.AddWithValue("$decision", (int)request.Result.Decision);
            park.Parameters.AddWithValue("$context", contextJson);
            park.Parameters.AddWithValue("$result", resultJson);
            park.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
            await park.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!request.IsCompensation)
            {
                await using var release = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    UPDATE workflow_steps SET status=$waiting, lease_owner=NULL, lease_expires_at=NULL
                    WHERE id=$claim AND status=$running AND lease_owner=$owner;
                    """);
                release.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
                release.Parameters.AddWithValue("$running", (int)StepStatus.Running);
                release.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(request.ClaimId));
                release.Parameters.AddWithValue("$owner", request.OwnerId);
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new WorkflowAuthorizationException("The parked step lease could not be released.");
            }
            else
            {
                await using var release = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    UPDATE workflow_step_compensations SET status=$pending, lease_owner=NULL, lease_expires_at=NULL
                    WHERE id=$claim AND status=$running AND lease_owner=$owner;
                    """);
                release.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
                release.Parameters.AddWithValue("$running", (int)CompensationStatus.Running);
                release.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(request.ClaimId));
                release.Parameters.AddWithValue("$owner", request.OwnerId);
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new WorkflowAuthorizationException("The parked compensation lease could not be released.");
            }
        }
        else if (request.Result.Decision == ExecutionAuthorizationDecision.Allowed)
        {
            await using var dispatch = SqliteStoreSupport.CreateCommand(connection, transaction, """
                INSERT INTO workflow_authorization_dispatches
                    (workflow_run_id,claim_id,is_compensation,last_started_attempt)
                VALUES ($run,$claim,$comp,$attempt)
                ON CONFLICT(workflow_run_id,claim_id,is_compensation) DO UPDATE
                SET last_started_attempt=excluded.last_started_attempt
                WHERE last_started_attempt < excluded.last_started_attempt;
                """);
            BindKey(dispatch, request.WorkflowRunId, request.ClaimId, request.IsCompensation);
            dispatch.Parameters.AddWithValue("$attempt", attempt);
            await dispatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var failure = new WorkflowError
            {
                Type = typeof(WorkflowAuthorizationException).FullName!,
                Message = $"Protected callback was not dispatched because authorization returned {request.Result.Decision}.",
                Timestamp = now,
                Attempt = attempt
            };
            var errorJson = SqliteStoreSupport.SerializeError(failure);
            if (request.IsCompensation)
            {
                await using var fail = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    UPDATE workflow_step_compensations SET status=$failed,error_json=$error,completed_at=$now,
                        available_at=NULL,lease_owner=NULL,lease_expires_at=NULL
                    WHERE id=$claim AND status=$running AND lease_owner=$owner;
                    """);
                fail.Parameters.AddWithValue("$failed", (int)CompensationStatus.Failed);
                fail.Parameters.AddWithValue("$running", (int)CompensationStatus.Running);
                fail.Parameters.AddWithValue("$error", SqliteStoreSupport.DbValue(errorJson));
                fail.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
                fail.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(request.ClaimId));
                fail.Parameters.AddWithValue("$owner", request.OwnerId);
                if (await fail.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new WorkflowAuthorizationException("Authorization failure could not settle the compensation claim.");
            }
            else
            {
                await using var fail = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    UPDATE workflow_steps SET status=$failed,error_json=$error,available_at=NULL,completed_at=$now,
                        lease_owner=NULL,lease_expires_at=NULL
                    WHERE id=$claim AND status=$running AND lease_owner=$owner;
                    """);
                fail.Parameters.AddWithValue("$failed", (int)StepStatus.Failed);
                fail.Parameters.AddWithValue("$running", (int)StepStatus.Running);
                fail.Parameters.AddWithValue("$error", SqliteStoreSupport.DbValue(errorJson));
                fail.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
                fail.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(request.ClaimId));
                fail.Parameters.AddWithValue("$owner", request.OwnerId);
                if (await fail.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new WorkflowAuthorizationException("Authorization failure could not settle the step claim.");
                await using var skip = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    UPDATE workflow_step_compensations SET status=$skipped
                    WHERE workflow_run_id=$run AND step_key=$step AND revision=$revision AND status=$pending;
                    """);
                skip.Parameters.AddWithValue("$skipped", (int)CompensationStatus.Skipped);
                skip.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
                skip.Parameters.AddWithValue("$step", request.StepKey);
                skip.Parameters.AddWithValue("$revision", request.Revision);
                skip.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
                await skip.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> WakeAuthorizationAsync(WorkflowAuthorizationWake request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkflowRunId == Guid.Empty || !IsBoundedId(request.AuthorizationRequestId) ||
            !IsBoundedId(request.ApprovalRequestId) || !IsBoundedId(request.ProviderId) ||
            !IsBoundedId(request.BindingId) || !IsHash(request.ContextHash) || request.Now == default) return false;
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var duplicate = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT accepted,provider_id,binding_id,context_hash FROM workflow_authorization_wake_receipts
            WHERE workflow_run_id=$run AND authorization_request_id=$request AND approval_request_id=$approval;
            """))
        {
            duplicate.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
            duplicate.Parameters.AddWithValue("$request", request.AuthorizationRequestId);
            duplicate.Parameters.AddWithValue("$approval", request.ApprovalRequestId);
            await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var same = reader.GetInt32(0) == 1 && reader.GetString(1) == request.ProviderId &&
                    reader.GetString(2) == request.BindingId && reader.GetString(3) == request.ContextHash;
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return same;
            }
        }
        var changed = 0;
        await using (var wake = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_authorization_checkpoints SET ready=1
            WHERE workflow_run_id=$run AND authorization_request_id=$request
              AND approval_request_id=$approval AND provider_id=$provider AND binding_id=$binding
              AND context_hash=$contextHash AND decision=$decision AND ready=0
              AND EXISTS (SELECT 1 FROM workflow_runs r WHERE r.id=$run AND r.status IN (0,1,2,3,6)
                AND r.authorization_provider_id=$provider AND r.authorization_binding_id=$binding
                AND r.lease_generation=workflow_authorization_checkpoints.lease_generation)
              AND ((is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s WHERE s.id=claim_id
                    AND s.workflow_run_id=$run AND s.step_key=workflow_authorization_checkpoints.step_key
                    AND s.revision=workflow_authorization_checkpoints.revision AND s.status IN ($waiting,$running)))
                OR (is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c
                    WHERE c.id=claim_id AND c.workflow_run_id=$run
                    AND c.step_key=workflow_authorization_checkpoints.step_key
                    AND c.revision=workflow_authorization_checkpoints.revision AND c.status IN ($pending,$compRunning))));
            """))
        {
            wake.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
            wake.Parameters.AddWithValue("$request", request.AuthorizationRequestId);
            wake.Parameters.AddWithValue("$approval", request.ApprovalRequestId);
            wake.Parameters.AddWithValue("$provider", request.ProviderId);
            wake.Parameters.AddWithValue("$binding", request.BindingId);
            wake.Parameters.AddWithValue("$contextHash", request.ContextHash);
            wake.Parameters.AddWithValue("$decision", (int)ExecutionAuthorizationDecision.ApprovalRequired);
            wake.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
            wake.Parameters.AddWithValue("$running", (int)StepStatus.Running);
            wake.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
            wake.Parameters.AddWithValue("$compRunning", (int)CompensationStatus.Running);
            changed = await wake.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using (var receipt = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_authorization_wake_receipts
            (workflow_run_id,authorization_request_id,approval_request_id,provider_id,binding_id,context_hash,accepted,created_at)
            VALUES ($run,$request,$approval,$provider,$binding,$contextHash,$accepted,$now);
            """))
        {
            receipt.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
            receipt.Parameters.AddWithValue("$request", request.AuthorizationRequestId);
            receipt.Parameters.AddWithValue("$approval", request.ApprovalRequestId);
            receipt.Parameters.AddWithValue("$provider", request.ProviderId);
            receipt.Parameters.AddWithValue("$binding", request.BindingId);
            receipt.Parameters.AddWithValue("$contextHash", request.ContextHash);
            receipt.Parameters.AddWithValue("$accepted", 1);
            receipt.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(request.Now));
            await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async ValueTask<bool> HasPendingAuthorizationsAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT EXISTS(SELECT 1 FROM workflow_authorization_checkpoints p
             JOIN workflow_runs r ON r.id=p.workflow_run_id
             WHERE p.workflow_run_id=$run AND p.decision=$approval
               AND ((p.is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s WHERE s.id=p.claim_id
                    AND s.revision=p.revision AND s.status IN ($running,$waiting) AND ((p.ready=1) OR
                      (s.lease_generation=p.lease_generation AND r.lease_generation=p.lease_generation))))
                 OR (p.is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c
                    WHERE c.id=p.claim_id AND c.revision=p.revision AND c.status IN ($compRunning,$compPending) AND ((p.ready=1) OR
                      (c.lease_generation=p.lease_generation AND r.lease_generation=p.lease_generation))))));
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$approval", (int)ExecutionAuthorizationDecision.ApprovalRequired);
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        command.Parameters.AddWithValue("$compRunning", (int)CompensationStatus.Running);
        command.Parameters.AddWithValue("$compPending", (int)CompensationStatus.Pending);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async ValueTask<bool> ValidateAuthorizationDispatchAsync(
        WorkflowAuthorizationCommit request, CancellationToken cancellationToken)
    {
        if (request is null || request.Result.Decision != ExecutionAuthorizationDecision.Allowed)
            return false;
        try { ValidateRequest(request); }
        catch (WorkflowAuthorizationException) { return false; }
        if (request.Result.AuthorizationRequestId != request.Context.AuthorizationRequestId ||
            !IsHash(request.ContextHash) ||
            request.ContextHash != WorkflowAuthorizationCodec.Context(request.BindingId, request.Context))
            return false;
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = factory.TimeProvider.GetUtcNow();
        if (request.Result.ExpiresAt is not { } expiry || expiry <= now)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var run = await ReadRunAsync(connection, transaction, request.WorkflowRunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.AuthorizationProviderId != request.Result.ProviderId ||
            run.AuthorizationBindingId != request.BindingId || run.LeaseOwner != request.OwnerId ||
            run.LeaseExpiresAt is not { } runExpiry || runExpiry <= now ||
            run.LeaseGeneration != request.LeaseGeneration)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var validRunStatus = request.IsCompensation
            ? run.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.RollingBack
            : run.Status is WorkflowStatus.Pending or WorkflowStatus.Running;
        if (!validRunStatus)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        try
        {
            await ValidateActiveGenerationAsync(connection, transaction, request.WorkflowRunId, request.IsCompensation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowAuthorizationException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var claim = await ReadClaimAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        var attempt = request.Context.Identity.Attempt;
        if (claim is null || claim.Value.Owner != request.OwnerId || claim.Value.LeaseExpiry <= now ||
            claim.Value.Generation != request.LeaseGeneration || claim.Value.Revision != request.Revision ||
            claim.Value.StepKey != request.StepKey || claim.Value.Hash != request.DeclarationHash ||
            claim.Value.Status != (request.IsCompensation ? (int)CompensationStatus.Running : (int)StepStatus.Running) ||
            claim.Value.Attempt != attempt ||
            request.ContextHash != WorkflowAuthorizationCodec.Context(request.BindingId, request.Context))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using var evidence = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM workflow_authorization_evidence e
                WHERE e.workflow_run_id=$run AND e.claim_id=$claim AND e.is_compensation=$comp
                  AND e.step_key=$step AND e.revision=$revision AND e.lease_generation=$generation
                  AND e.attempt=$attempt AND e.provider_id=$provider AND e.binding_id=$binding
                  AND e.declaration_hash=$declaration AND e.context_hash=$contextHash
                  AND e.authorization_request_id=$requestId AND e.decision=$allowed)
             AND EXISTS(SELECT 1 FROM workflow_authorization_dispatches d
                WHERE d.workflow_run_id=$run AND d.claim_id=$claim AND d.is_compensation=$comp
                  AND d.last_started_attempt=$attempt)
             AND NOT EXISTS(SELECT 1 FROM workflow_authorization_checkpoints p
                WHERE p.workflow_run_id=$run AND p.claim_id=$claim AND p.is_compensation=$comp);
            """);
        BindKey(evidence, request.WorkflowRunId, request.ClaimId, request.IsCompensation);
        evidence.Parameters.AddWithValue("$step", request.StepKey);
        evidence.Parameters.AddWithValue("$revision", request.Revision);
        evidence.Parameters.AddWithValue("$generation", request.LeaseGeneration);
        evidence.Parameters.AddWithValue("$attempt", attempt);
        evidence.Parameters.AddWithValue("$provider", request.Result.ProviderId);
        evidence.Parameters.AddWithValue("$binding", request.BindingId);
        evidence.Parameters.AddWithValue("$declaration", request.DeclarationHash);
        evidence.Parameters.AddWithValue("$contextHash", request.ContextHash);
        evidence.Parameters.AddWithValue("$requestId", request.Result.AuthorizationRequestId);
        evidence.Parameters.AddWithValue("$allowed", (int)ExecutionAuthorizationDecision.Allowed);
        var found = Convert.ToInt32(await evidence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
        if (!found)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> RenewAuthorizationClaimLeaseAsync(Guid workflowRunId, Guid claimId,
        bool isCompensation, string ownerId, long leaseGeneration, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty || claimId == Guid.Empty || string.IsNullOrWhiteSpace(ownerId) ||
            leaseGeneration < 1 || expiresAt <= now)
            throw new ArgumentException("Authorization lease renewal identity or expiry is invalid.");
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var effectiveNow = factory.TimeProvider.GetUtcNow();
        if (expiresAt <= effectiveNow)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await using (var run = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs SET lease_expires_at=CASE WHEN lease_expires_at>$expires THEN lease_expires_at ELSE $expires END,updated_at=$now
            WHERE id=$run AND lease_owner=$owner AND lease_generation=$generation
              AND lease_expires_at>$now AND status IN ($pending,$running,$completed,$failed,$rollingBack);
            """))
        {
            run.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(expiresAt));
            run.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(effectiveNow));
            run.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
            run.Parameters.AddWithValue("$owner", ownerId);
            run.Parameters.AddWithValue("$generation", leaseGeneration);
            run.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
            run.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
            run.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
            run.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
            run.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
            if (await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        var table = isCompensation ? "workflow_step_compensations" : "workflow_steps";
        var statusClause = isCompensation ? "status=$compRunning" : "status=$stepRunning";
        await using (var claim = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            UPDATE {table} SET lease_expires_at=CASE WHEN lease_expires_at>$expires THEN lease_expires_at ELSE $expires END
            WHERE id=$claim AND workflow_run_id=$run AND lease_owner=$owner
              AND lease_generation=$generation AND lease_expires_at>$now AND {statusClause};
            """))
        {
            claim.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(expiresAt));
            claim.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(effectiveNow));
            claim.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(claimId));
            claim.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
            claim.Parameters.AddWithValue("$owner", ownerId);
            claim.Parameters.AddWithValue("$generation", leaseGeneration);
            claim.Parameters.AddWithValue("$compRunning", (int)CompensationStatus.Running);
            claim.Parameters.AddWithValue("$stepRunning", (int)StepStatus.Running);
            if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<WorkflowRun?> ReadRunAsync(SqliteConnection connection, SqliteTransaction tx, Guid runId, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, tx, $"SELECT {SqliteStoreSupport.RunColumns} FROM workflow_runs WHERE id=$id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(runId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? SqliteStoreSupport.ReadRun(reader) : null;
    }

    private static async ValueTask<(string Owner, DateTimeOffset LeaseExpiry, long Generation, int Revision, string StepKey, string? Hash, int Status, int Attempt)?> ReadClaimAsync(
        SqliteConnection connection, SqliteTransaction tx, WorkflowAuthorizationCommit request, CancellationToken ct)
    {
        var sql = request.IsCompensation ? """
            SELECT c.lease_owner,c.lease_expires_at,c.lease_generation,c.revision,c.step_key,
                   c.authorization_declaration_json,c.status,c.attempt
            FROM workflow_step_compensations c WHERE id=$claim AND workflow_run_id=$run;
            """ : """
            SELECT lease_owner,lease_expires_at,lease_generation,revision,step_key,authorization_declaration_hash,status,attempt
            FROM workflow_steps WHERE id=$claim AND workflow_run_id=$run;
            """;
        await using var command = SqliteStoreSupport.CreateCommand(connection, tx, sql);
        command.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(request.ClaimId));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1)) return null;
        var row = (reader.GetString(0), SqliteStoreSupport.ParseTimestamp(reader.GetString(1)), reader.GetInt64(2),
            reader.GetInt32(3), reader.GetString(4), SqliteStoreSupport.GetNullableString(reader, 5), reader.GetInt32(6), reader.GetInt32(7));
        if (request.IsCompensation && row.Item6 is { } declarationJson)
            return (row.Item1, row.Item2, row.Item3, row.Item4, row.Item5,
                WorkflowAuthorizationCodec.Declaration(null, WorkflowAuthorizationCodec.ReadDeclaration(declarationJson)), row.Item7, row.Item8);
        return row;
    }

    private static async ValueTask ValidateActiveGenerationAsync(SqliteConnection connection, SqliteTransaction tx, Guid runId, bool compensation, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, tx, """
            SELECT g.status,g.ordinal,g.instance_id,
              EXISTS(SELECT 1 FROM workflow_generations newer WHERE newer.instance_id=g.instance_id
                AND newer.status=$active AND newer.ordinal>g.ordinal)
            FROM workflow_generations g WHERE g.workflow_run_id=$run
            ORDER BY CASE WHEN g.status IN (2,5) THEN 0 ELSE 1 END, g.ordinal DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new WorkflowAuthorizationException("Protected execution has no bound workflow generation.");
        var generationStatus = (WorkflowGenerationStatus)reader.GetInt32(0);
        var futureOwner = reader.GetInt32(3) != 0;
        var allowed = compensation
            ? !futureOwner && generationStatus is (WorkflowGenerationStatus.Active or WorkflowGenerationStatus.Quiescing)
            : generationStatus == WorkflowGenerationStatus.Active;
        if (!allowed)
            throw new WorkflowAuthorizationException("Bound execution generation is no longer active for this operation.");
    }

    private static async ValueTask<int> GetNextAttemptAsync(SqliteConnection connection, SqliteTransaction tx, Guid runId, Guid claimId, bool comp, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, tx, """
            SELECT last_started_attempt+1 FROM workflow_authorization_dispatches
            WHERE workflow_run_id=$run AND claim_id=$claim AND is_compensation=$comp;
            """);
        BindKey(command, runId, claimId, comp);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 1 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsBoundedId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
            return false;
        try { return StrictUtf8.GetByteCount(value) <= 256; }
        catch (EncoderFallbackException) { return false; }
    }
    private static void ValidateRequest(WorkflowAuthorizationCommit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkflowRunId == Guid.Empty || request.ClaimId == Guid.Empty || request.Revision < 1 || request.LeaseGeneration < 1)
            throw new WorkflowAuthorizationException("Authorization claim identity is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepKey);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Result);
        if (request.Result.Decision == ExecutionAuthorizationDecision.ApprovalRequired && string.IsNullOrWhiteSpace(request.Result.ApprovalRequestId))
            throw new WorkflowAuthorizationException("ApprovalRequired requires an approval request correlation.");
        if (request.Result.Decision == ExecutionAuthorizationDecision.Allowed &&
            (request.Result.ExpiresAt is not { } expiry || expiry <= request.Now))
            throw new WorkflowAuthorizationException("Allowed result has expired before commit.");
    }

    private static void BindKey(SqliteCommand command, Guid runId, Guid claimId, bool comp)
    {
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$claim", SqliteStoreSupport.Format(claimId));
        command.Parameters.AddWithValue("$comp", comp ? 1 : 0);
    }
}
