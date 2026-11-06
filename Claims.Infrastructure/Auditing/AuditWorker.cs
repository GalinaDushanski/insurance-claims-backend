namespace Claims.Auditing;

public sealed class AuditWorker(
    AuditQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // StopAsync closes the queue; only the shutdown deadline cancels draining.
        var cancellationToken = _abort.Token;
        try
        {
            await foreach (var auditEvent in queue.ReadAllAsync(cancellationToken))
                await ProcessAsync(auditEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Audit worker shutdown interrupted draining. Unwritten events may be lost.");
        }
    }

    private async Task ProcessAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
                await writer.WriteAsync(auditEvent, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Audit write cancelled for {EntityType} {EntityId} {HttpRequestType}, created {Created}, on attempt {Attempt}.",
                    auditEvent.EntityType, auditEvent.EntityId, auditEvent.HttpRequestType, auditEvent.Created, attempt);
                throw;
            }
            catch (Exception exception)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(exception,
                        "Audit event abandoned for {EntityType} {EntityId} {HttpRequestType}, created {Created}, after attempt {Attempt}.",
                        auditEvent.EntityType, auditEvent.EntityId, auditEvent.HttpRequestType, auditEvent.Created, attempt);
                    return;
                }

                logger.LogWarning(exception,
                    "Audit write failed for {EntityType} {EntityId} {HttpRequestType}, created {Created}, on attempt {Attempt}. Retrying.",
                    auditEvent.EntityType, auditEvent.EntityId, auditEvent.HttpRequestType, auditEvent.Created, attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        using var registration = cancellationToken.Register(() => _abort.Cancel());
        try
        {
            // Drain before base.StopAsync can cancel execution still waiting to start.
            if (ExecuteTask is { } execution)
                await execution.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            // The deadline wait can finish before its cancellation callback runs.
            if (cancellationToken.IsCancellationRequested) _abort.Cancel();
            await base.StopAsync(cancellationToken);
        }
    }

    public override void Dispose()
    {
        queue.Complete();
        _abort.Cancel();
        base.Dispose();
        _abort.Dispose();
    }
}
