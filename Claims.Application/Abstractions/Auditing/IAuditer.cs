namespace Claims.Auditing;

public interface IAuditer
{
    Task AuditClaimAsync(string id, string httpRequestType, CancellationToken cancellationToken = default);
    Task AuditCoverAsync(string id, string httpRequestType, CancellationToken cancellationToken = default);
}
