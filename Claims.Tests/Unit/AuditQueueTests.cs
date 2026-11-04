using System.Threading.Channels;
using Claims.Auditing;
using Xunit;

namespace Claims.Tests.Unit;

public class AuditQueueTests
{
    [Fact]
    public async Task Events_preserve_payload_and_fifo_order()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var expected = new[]
        {
            NewEvent("claim-1"),
            NewEvent("cover-1", AuditEntityType.Cover, "DELETE"),
            NewEvent("claim-2", AuditEntityType.Claim, "DELETE")
        };
        foreach (var auditEvent in expected)
            await queue.EnqueueAsync(auditEvent, timeout.Token);
        queue.Complete();

        var actual = await DrainAsync(queue, timeout.Token);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Concurrent_producers_do_not_lose_or_duplicate_events_and_preserve_each_producers_order()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        const int producerCount = 8;
        const int eventsPerProducer = 250;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumption = DrainAsync(queue, timeout.Token);
        var producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(async () =>
        {
            await start.Task.WaitAsync(timeout.Token);
            for (var sequence = 0; sequence < eventsPerProducer; sequence++)
                await queue.EnqueueAsync(NewEvent($"{producer}:{sequence}"), timeout.Token);
        }, timeout.Token)).ToArray();

        start.SetResult();
        await Task.WhenAll(producers).WaitAsync(timeout.Token);
        queue.Complete();
        var events = await consumption.WaitAsync(timeout.Token);

        Assert.Equal(producerCount * eventsPerProducer, events.Count);
        Assert.Equal(events.Count, events.Select(item => item.EntityId).Distinct().Count());
        for (var producer = 0; producer < producerCount; producer++)
            Assert.Equal(Enumerable.Range(0, eventsPerProducer), events
                .Where(item => item.EntityId.StartsWith($"{producer}:"))
                .Select(item => int.Parse(item.EntityId.Split(':')[1])));
    }

    [Fact]
    public async Task Enqueue_waits_when_1000_events_are_buffered_and_resumes_when_a_reader_frees_space()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        await FillAsync(queue, timeout.Token);

        var pending = queue.EnqueueAsync(NewEvent("1000"), timeout.Token).AsTask();
        Assert.False(pending.IsCompleted);

        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("0", reader.Current.EntityId);
        await pending.WaitAsync(timeout.Token);
        queue.Complete();
        var ids = new List<string> { reader.Current.EntityId };
        while (await reader.MoveNextAsync()) ids.Add(reader.Current.EntityId);

        Assert.Equal(Enumerable.Range(0, 1001).Select(id => id.ToString()), ids);
    }

    [Fact]
    public async Task Cancellation_rejects_a_waiting_enqueue_without_adding_its_event()
    {
        using var timeout = CreateTimeout();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var queue = new AuditQueue();
        await FillAsync(queue, timeout.Token);
        var pending = queue.EnqueueAsync(NewEvent("cancelled"), waiting.Token).AsTask();
        Assert.False(pending.IsCompleted);

        await waiting.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        queue.Complete();
        var events = await DrainAsync(queue, timeout.Token);

        Assert.Equal(waiting.Token, error.CancellationToken);
        Assert.Equal(1000, events.Count);
        Assert.DoesNotContain(events, item => item.EntityId == "cancelled");
    }

    [Fact]
    public async Task Completion_is_idempotent_drains_accepted_events_and_rejects_new_events()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var accepted = NewEvent("accepted");
        await queue.EnqueueAsync(accepted, timeout.Token);

        queue.Complete();
        queue.Complete();

        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            queue.EnqueueAsync(NewEvent("rejected"), timeout.Token).AsTask());
        Assert.Same(accepted, Assert.Single(await DrainAsync(queue, timeout.Token)));
    }

    [Fact]
    public async Task Completion_rejects_a_producer_already_waiting_for_capacity()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        await FillAsync(queue, timeout.Token);
        var pending = queue.EnqueueAsync(NewEvent("rejected"), timeout.Token).AsTask();
        Assert.False(pending.IsCompleted);

        queue.Complete();

        await Assert.ThrowsAsync<ChannelClosedException>(() => pending);
        Assert.Equal(1000, (await DrainAsync(queue, timeout.Token)).Count);
    }

    [Fact]
    public async Task Empty_queue_waits_for_an_event_and_consumption_finishes_after_completion()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pendingRead = reader.MoveNextAsync().AsTask();
        Assert.False(pendingRead.IsCompleted);
        var auditEvent = NewEvent("arrived");

        await queue.EnqueueAsync(auditEvent, timeout.Token);

        Assert.True(await pendingRead.WaitAsync(timeout.Token));
        Assert.Same(auditEvent, reader.Current);
        queue.Complete();
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_interrupts_a_consumer_waiting_on_an_empty_queue()
    {
        using var timeout = CreateTimeout();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var queue = new AuditQueue();
        await using var reader = queue.ReadAllAsync(waiting.Token).GetAsyncEnumerator(waiting.Token);
        var pendingRead = reader.MoveNextAsync().AsTask();
        Assert.False(pendingRead.IsCompleted);

        await waiting.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
        queue.Complete();
    }

    private static async Task FillAsync(AuditQueue queue, CancellationToken cancellationToken)
    {
        for (var id = 0; id < 1000; id++)
            await queue.EnqueueAsync(NewEvent(id.ToString()), cancellationToken);
    }

    private static async Task<List<AuditEvent>> DrainAsync(AuditQueue queue, CancellationToken cancellationToken)
    {
        var events = new List<AuditEvent>();
        await foreach (var auditEvent in queue.ReadAllAsync(cancellationToken)) events.Add(auditEvent);
        return events;
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static AuditEvent NewEvent(string id, AuditEntityType type = AuditEntityType.Claim, string operation = "POST") =>
        new(type, id, operation, new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc));
}
