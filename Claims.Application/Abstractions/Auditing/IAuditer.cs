namespace Claims.Auditing;

public interface IAuditer
{
    Task AuditClaimAsync(string id, AuditAction action, CancellationToken cancellationToken = default);
    Task AuditCoverAsync(string id, AuditAction action, CancellationToken cancellationToken = default);
}
