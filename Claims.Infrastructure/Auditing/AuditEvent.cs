namespace Claims.Auditing;

public enum AuditEntityType
{
    Claim,
    Cover
}

public sealed record AuditEvent(
    AuditEntityType EntityType,
    string EntityId,
    AuditAction Action,
    DateTime Created);
