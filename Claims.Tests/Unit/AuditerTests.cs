using System.Threading.Channels;
using Claims.Auditing;
using Xunit;

namespace Claims.Tests.Unit;

public class AuditerTests
{
    [Theory]
    [InlineData(AuditEntityType.Claim, "POST")]
    [InlineData(AuditEntityType.Claim, "DELETE")]
    [InlineData(AuditEntityType.Cover, "POST")]
    [InlineData(AuditEntityType.Cover, "DELETE")]
    public async Task Publishes_entity_id_operation_and_local_wall_clock_timestamp(AuditEntityType type, string operation)
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var clock = NewClock();
        var auditer = new Auditer(queue, clock);

        await PublishAsync(auditer, type, "entity-id", operation, timeout.Token);
        queue.Complete();

        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(type, reader.Current.EntityType);
        Assert.Equal("entity-id", reader.Current.EntityId);
        Assert.Equal(operation, reader.Current.HttpRequestType);
        Assert.Equal(new DateTime(2030, 1, 2, 1, 30, 0), reader.Current.Created);
        Assert.Equal(DateTimeKind.Unspecified, reader.Current.Created.Kind);
        Assert.False(await reader.MoveNextAsync());
    }

    [Theory]
    [InlineData(AuditEntityType.Claim)]
    [InlineData(AuditEntityType.Cover)]
    public async Task Waits_for_queue_acceptance_and_captures_timestamp_before_waiting(AuditEntityType type)
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var clock = NewClock();
        var expectedTimestamp = clock.GetLocalNow().DateTime;
        var auditer = new Auditer(queue, clock);
        await FillAsync(queue, timeout.Token);

        var publishing = PublishAsync(auditer, type, "published", "POST", timeout.Token);
        Assert.False(publishing.IsCompleted);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        await publishing.WaitAsync(timeout.Token);
        queue.Complete();
        AuditEvent? published = null;
        while (await reader.MoveNextAsync())
            if (reader.Current.EntityId == "published") published = reader.Current;

        Assert.NotNull(published);
        Assert.Equal(type, published.EntityType);
        Assert.Equal(expectedTimestamp, published.Created);
        Assert.NotEqual(clock.GetLocalNow().DateTime, published.Created);
    }

    [Theory]
    [InlineData(AuditEntityType.Claim)]
    [InlineData(AuditEntityType.Cover)]
    public async Task Closed_queue_rejects_publication(AuditEntityType type)
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        queue.Complete();

        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            PublishAsync(new Auditer(queue, NewClock()), type, "rejected", "POST", timeout.Token));
    }

    [Theory]
    [InlineData(AuditEntityType.Claim)]
    [InlineData(AuditEntityType.Cover)]
    public async Task Cancellation_of_waiting_publication_does_not_enqueue_the_event(AuditEntityType type)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var queue = new AuditQueue();
        await FillAsync(queue, timeout.Token);
        var publishing = PublishAsync(new Auditer(queue, NewClock()), type, "cancelled", "POST", cancellation.Token);
        Assert.False(publishing.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
        queue.Complete();
        var count = 0;
        await foreach (var auditEvent in queue.ReadAllAsync(timeout.Token))
        {
            Assert.NotEqual("cancelled", auditEvent.EntityId);
            count++;
        }
        Assert.Equal(1000, count);
    }

    private static Task PublishAsync(IAuditer auditer, AuditEntityType type, string id, string operation, CancellationToken token) =>
        type == AuditEntityType.Claim
            ? auditer.AuditClaimAsync(id, operation, token)
            : auditer.AuditCoverAsync(id, operation, token);

    private static async Task FillAsync(AuditQueue queue, CancellationToken token)
    {
        for (var id = 0; id < 1000; id++)
            await queue.EnqueueAsync(new AuditEvent(AuditEntityType.Claim, id.ToString(), "POST", DateTime.MinValue), token);
    }

    private static FixedTimeProvider NewClock() => new(
        new DateTimeOffset(2030, 1, 1, 22, 30, 0, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone("Audit tests", TimeSpan.FromHours(3), "Audit tests", "Audit tests"));

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }
}
