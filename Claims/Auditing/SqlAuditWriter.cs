namespace Claims.Auditing;

public sealed class SqlAuditWriter(AuditContext context) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (auditEvent.EntityType)
        {
            case AuditEntityType.Claim:
                context.ClaimAudits.Add(new ClaimAudit
                {
                    ClaimId = auditEvent.EntityId,
                    HttpRequestType = auditEvent.HttpRequestType,
                    Created = auditEvent.Created
                });
                break;
            case AuditEntityType.Cover:
                context.CoverAudits.Add(new CoverAudit
                {
                    CoverId = auditEvent.EntityId,
                    HttpRequestType = auditEvent.HttpRequestType,
                    Created = auditEvent.Created
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(auditEvent), auditEvent.EntityType,
                    "Unknown audit entity type.");
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
