namespace Claims.Auditing;

public sealed class Auditer(AuditQueue queue, TimeProvider timeProvider) : IAuditer
{
    public Task AuditClaimAsync(string id, AuditAction action, CancellationToken cancellationToken = default) =>
        PublishAsync(AuditEntityType.Claim, id, action, cancellationToken);

    public Task AuditCoverAsync(string id, AuditAction action, CancellationToken cancellationToken = default) =>
        PublishAsync(AuditEntityType.Cover, id, action, cancellationToken);

    private async Task PublishAsync(AuditEntityType entityType, string id, AuditAction action,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent(entityType, id, action, timeProvider.GetLocalNow().DateTime);
        await queue.EnqueueAsync(auditEvent, cancellationToken);
    }
}
