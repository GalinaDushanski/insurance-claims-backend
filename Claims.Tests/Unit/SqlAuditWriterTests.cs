using Claims.Auditing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Claims.Tests.Unit;

public class SqlAuditWriterTests
{
    [Theory]
    [InlineData(AuditEntityType.Claim, AuditAction.Created, "POST")]
    [InlineData(AuditEntityType.Claim, AuditAction.Deleted, "DELETE")]
    [InlineData(AuditEntityType.Cover, AuditAction.Created, "POST")]
    [InlineData(AuditEntityType.Cover, AuditAction.Deleted, "DELETE")]
    public async Task Maps_events_to_added_audit_entities_and_preserves_existing_tables_and_fields(
        AuditEntityType type, AuditAction action, string expectedLegacyValue)
    {
        await using var context = new CapturingAuditContext();
        var writer = new SqlAuditWriter(context);
        var created = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var auditEvent = new AuditEvent(type, "entity-id", action, created);
        var token = TestContext.Current.CancellationToken;

        await writer.WriteAsync(auditEvent, token);

        Assert.Equal(1, context.SaveCalls);
        Assert.Equal(token, context.SavedToken);
        var entry = Assert.Single(context.SavedEntries);
        Assert.Equal(EntityState.Added, entry.State);
        if (type == AuditEntityType.Claim)
        {
            var audit = Assert.IsType<ClaimAudit>(entry.Entity);
            Assert.Equal("entity-id", audit.ClaimId);
            Assert.Equal(expectedLegacyValue, audit.HttpRequestType);
            Assert.Equal(created, audit.Created);
            Assert.Equal(created.Kind, audit.Created.Kind);
            Assert.Equal("ClaimAudits", context.Model.FindEntityType(typeof(ClaimAudit))!.GetTableName());
            Assert.Empty(context.ChangeTracker.Entries<CoverAudit>());
        }
        else
        {
            var audit = Assert.IsType<CoverAudit>(entry.Entity);
            Assert.Equal("entity-id", audit.CoverId);
            Assert.Equal(expectedLegacyValue, audit.HttpRequestType);
            Assert.Equal(created, audit.Created);
            Assert.Equal(created.Kind, audit.Created.Kind);
            Assert.Equal("CoverAudits", context.Model.FindEntityType(typeof(CoverAudit))!.GetTableName());
            Assert.Empty(context.ChangeTracker.Entries<ClaimAudit>());
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public async Task Uses_the_event_timestamp_without_replacing_or_converting_it(DateTimeKind kind)
    {
        await using var context = new CapturingAuditContext();
        var created = new DateTime(2030, 1, 1, 12, 0, 0, kind);

        await new SqlAuditWriter(context).WriteAsync(
            new AuditEvent(AuditEntityType.Claim, "claim", AuditAction.Created, created), TestContext.Current.CancellationToken);

        var audit = Assert.IsType<ClaimAudit>(Assert.Single(context.SavedEntries).Entity);
        Assert.Equal(created, audit.Created);
        Assert.Equal(kind, audit.Created.Kind);
    }

    [Fact]
    public async Task Already_cancelled_write_does_not_add_or_save_an_audit_entity()
    {
        await using var context = new CapturingAuditContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlAuditWriter(context).WriteAsync(
            new AuditEvent(AuditEntityType.Claim, "claim", AuditAction.Deleted, DateTime.Now), cancellation.Token));

        Assert.Equal(0, context.SaveCalls);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Write_waits_for_async_save_completion()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var context = new CapturingAuditContext();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.SaveCompletion = completion;

        var writing = new SqlAuditWriter(context).WriteAsync(
            new AuditEvent(AuditEntityType.Claim, "claim", AuditAction.Created, DateTime.Now), timeout.Token);

        Assert.False(writing.IsCompleted);
        Assert.Equal(1, context.SaveCalls);
        completion.SetResult();
        await writing.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Cancellation_reaches_an_inflight_async_save()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await using var context = new CapturingAuditContext();
        context.SaveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new SqlAuditWriter(context).WriteAsync(
            new AuditEvent(AuditEntityType.Cover, "cover", AuditAction.Deleted, DateTime.Now), cancellation.Token);
        Assert.False(writing.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal(cancellation.Token, context.SavedToken);
    }

    [Fact]
    public async Task Unknown_entity_type_is_rejected_without_saving()
    {
        await using var context = new CapturingAuditContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SqlAuditWriter(context).WriteAsync(
            new AuditEvent((AuditEntityType)99, "unknown", AuditAction.Created, DateTime.Now), TestContext.Current.CancellationToken));

        Assert.Equal(0, context.SaveCalls);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(AuditEntityType.Claim)]
    [InlineData(AuditEntityType.Cover)]
    public async Task Undefined_action_is_rejected_without_tracking_or_saving(AuditEntityType type)
    {
        await using var context = new CapturingAuditContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SqlAuditWriter(context).WriteAsync(
            new AuditEvent(type, "unknown-action", (AuditAction)99, DateTime.Now), TestContext.Current.CancellationToken));

        Assert.Equal(0, context.SaveCalls);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private sealed class CapturingAuditContext() : AuditContext(
        new DbContextOptionsBuilder<AuditContext>()
            .UseSqlServer("Server=unused;Database=audit-tests;Integrated Security=True")
            .Options)
    {
        public int SaveCalls { get; private set; }
        public TaskCompletionSource? SaveCompletion { get; set; }
        public CancellationToken SavedToken { get; private set; }
        public List<(object Entity, EntityState State)> SavedEntries { get; private set; } = [];

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            SavedToken = cancellationToken;
            SavedEntries = ChangeTracker.Entries().Select(entry => (entry.Entity, entry.State)).ToList();
            if (SaveCompletion is not null) await SaveCompletion.Task.WaitAsync(cancellationToken);
            return SavedEntries.Count;
        }
    }
}
