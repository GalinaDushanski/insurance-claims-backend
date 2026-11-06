namespace Claims.Auditing;

public sealed class Auditer(AuditQueue queue, TimeProvider timeProvider) : IAuditer
{
    public Task AuditClaimAsync(string id, string httpRequestType, CancellationToken cancellationToken = default) =>
        PublishAsync(AuditEntityType.Claim, id, httpRequestType, cancellationToken);

    public Task AuditCoverAsync(string id, string httpRequestType, CancellationToken cancellationToken = default) =>
        PublishAsync(AuditEntityType.Cover, id, httpRequestType, cancellationToken);

    private async Task PublishAsync(AuditEntityType entityType, string id, string httpRequestType,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent(entityType, id, httpRequestType, timeProvider.GetLocalNow().DateTime);
        await queue.EnqueueAsync(auditEvent, cancellationToken);
    }
}
