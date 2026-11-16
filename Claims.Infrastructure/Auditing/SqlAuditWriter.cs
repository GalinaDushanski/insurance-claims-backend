namespace Claims.Auditing;

public sealed class SqlAuditWriter(AuditContext context) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var httpRequestType = auditEvent.Action switch
        {
            AuditAction.Created => "POST",
            AuditAction.Deleted => "DELETE",
            _ => throw new ArgumentOutOfRangeException(nameof(auditEvent), auditEvent.Action,
                "Unknown audit action.")
        };

        switch (auditEvent.EntityType)
        {
            case AuditEntityType.Claim:
                context.ClaimAudits.Add(new ClaimAudit
                {
                    ClaimId = auditEvent.EntityId,
                    HttpRequestType = httpRequestType,
                    Created = auditEvent.Created
                });
                break;
            case AuditEntityType.Cover:
                context.CoverAudits.Add(new CoverAudit
                {
                    CoverId = auditEvent.EntityId,
                    HttpRequestType = httpRequestType,
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
