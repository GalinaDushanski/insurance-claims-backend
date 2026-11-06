using System.Collections.Concurrent;
using System.Threading.Channels;
using Claims.Auditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Claims.Tests.Unit;

public class AuditWorkerTests
{
    [Fact]
    public async Task Processes_events_sequentially_with_original_payload_and_disposes_each_scope()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        fixture.State.Write = async (_, scopeId, token) =>
        {
            if (scopeId == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
        };
        var first = NewEvent("claim", AuditEntityType.Claim, "POST");
        var second = NewEvent("cover", AuditEntityType.Cover, "DELETE");
        await fixture.Queue.EnqueueAsync(first, timeout.Token);
        await fixture.Queue.EnqueueAsync(second, timeout.Token);
        fixture.Queue.Complete();

        await fixture.Worker.StartAsync(timeout.Token);
        await firstStarted.Task.WaitAsync(timeout.Token);
        Assert.Same(first, Assert.Single(fixture.State.Attempts).Event);
        releaseFirst.SetResult();
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        var attempts = fixture.State.Attempts.ToArray();
        Assert.Equal(new[] { first, second }, attempts.Select(attempt => attempt.Event));
        Assert.Equal(2, attempts.Select(attempt => attempt.ScopeId).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, fixture.State.DisposedScopes);
        Assert.Empty(fixture.Logger.Entries);
    }

    [Fact]
    public async Task Failed_write_recovers_on_retry_with_a_fresh_scope_and_logs_event_identity()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        var failure = new InvalidOperationException("Temporary SQL failure");
        fixture.State.Write = (_, scopeId, _) => scopeId == 1 ? Task.FromException(failure) : Task.CompletedTask;
        var auditEvent = NewEvent("retry", AuditEntityType.Cover, "POST");
        await fixture.Queue.EnqueueAsync(auditEvent, timeout.Token);
        fixture.Queue.Complete();

        await fixture.Worker.StartAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        Assert.Equal(new[] { auditEvent, auditEvent }, fixture.State.Attempts.Select(attempt => attempt.Event));
        Assert.Equal(new[] { 1, 2 }, fixture.State.DisposedScopes);
        var warning = Assert.Single(fixture.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(failure, warning.Exception);
        Assert.Equal(1, warning.Properties["Attempt"]);
        Assert.Equal(auditEvent.EntityType, warning.Properties["EntityType"]);
        Assert.Equal(auditEvent.EntityId, warning.Properties["EntityId"]);
        Assert.Equal(auditEvent.HttpRequestType, warning.Properties["HttpRequestType"]);
        Assert.Equal(auditEvent.Created, warning.Properties["Created"]);
    }

    [Fact]
    public async Task Terminal_failure_stops_after_three_attempts_and_continues_with_the_next_event()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        fixture.State.Write = (auditEvent, _, _) => auditEvent.EntityId == "bad"
            ? Task.FromException(new InvalidOperationException("SQL failure")) : Task.CompletedTask;
        await fixture.Queue.EnqueueAsync(NewEvent("bad"), timeout.Token);
        await fixture.Queue.EnqueueAsync(NewEvent("next"), timeout.Token);
        fixture.Queue.Complete();

        await fixture.Worker.StartAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        Assert.Equal(new[] { "bad", "bad", "bad", "next" }, fixture.State.Attempts.Select(attempt => attempt.Event.EntityId));
        Assert.Equal(new[] { 1, 2, 3, 4 }, fixture.State.DisposedScopes);
        Assert.Equal(4, fixture.State.Attempts.Select(attempt => attempt.ScopeId).Distinct().Count());
        var logs = fixture.Logger.Entries.ToArray();
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Warning, LogLevel.Error }, logs.Select(entry => entry.Level));
        Assert.Equal(new object?[] { 1, 2, 3 }, logs.Select(entry => entry.Properties["Attempt"]));
        Assert.All(logs, entry => Assert.Equal("bad", entry.Properties["EntityId"]));
    }

    [Fact]
    public async Task Graceful_stop_rejects_new_events_and_drains_accepted_events_without_cancelling_sql()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        var started = NewSignal();
        var release = NewSignal();
        fixture.State.Write = async (_, scopeId, token) =>
        {
            if (scopeId == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await fixture.Queue.EnqueueAsync(NewEvent("first"), timeout.Token);
        await fixture.Queue.EnqueueAsync(NewEvent("second"), timeout.Token);
        await fixture.Worker.StartAsync(timeout.Token);
        await started.Task.WaitAsync(timeout.Token);

        var stopping = fixture.Worker.StopAsync(timeout.Token);

        Assert.False(stopping.IsCompleted);
        Assert.False(Assert.Single(fixture.State.Attempts).Token.IsCancellationRequested);
        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            fixture.Queue.EnqueueAsync(NewEvent("late"), timeout.Token).AsTask());
        release.SetResult();
        await stopping.WaitAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "first", "second" }, fixture.State.Attempts.Select(attempt => attempt.Event.EntityId));
        Assert.Equal(new[] { 1, 2 }, fixture.State.DisposedScopes);
        Assert.Empty(fixture.Logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_deadline_cancels_inflight_sql_and_does_not_process_the_backlog(bool alreadyCancelled)
    {
        using var timeout = CreateTimeout();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var fixture = new WorkerFixture();
        var started = NewSignal();
        fixture.State.Write = async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await fixture.Queue.EnqueueAsync(NewEvent("inflight"), timeout.Token);
        await fixture.Queue.EnqueueAsync(NewEvent("pending"), timeout.Token);
        await fixture.Worker.StartAsync(timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        if (alreadyCancelled) await deadline.CancelAsync();

        var stopping = fixture.Worker.StopAsync(deadline.Token);
        if (!alreadyCancelled)
        {
            Assert.False(stopping.IsCompleted);
            await deadline.CancelAsync();
        }
        await stopping.WaitAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        var attempt = Assert.Single(fixture.State.Attempts);
        Assert.Equal("inflight", attempt.Event.EntityId);
        Assert.True(attempt.Token.IsCancellationRequested);
        Assert.Equal(new[] { 1 }, fixture.State.DisposedScopes);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Properties.ContainsKey("EntityId"));
        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            fixture.Queue.EnqueueAsync(NewEvent("late"), timeout.Token).AsTask());
    }

    [Fact]
    public async Task Shutdown_deadline_interrupts_retry_delay_without_another_attempt()
    {
        using var timeout = CreateTimeout();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var fixture = new WorkerFixture();
        var failed = NewSignal();
        fixture.Logger.OnLog = entry =>
        {
            if (entry.Exception is not null) failed.TrySetResult();
        };
        fixture.State.Write = (_, _, _) => Task.FromException(new InvalidOperationException("SQL unavailable"));
        await fixture.Queue.EnqueueAsync(NewEvent("retry"), timeout.Token);
        await fixture.Worker.StartAsync(timeout.Token);
        await failed.Task.WaitAsync(timeout.Token);

        var stopping = fixture.Worker.StopAsync(deadline.Token);
        await deadline.CancelAsync();
        await stopping.WaitAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        Assert.Single(fixture.State.Attempts);
        Assert.Equal(new[] { 1 }, fixture.State.DisposedScopes);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Immediate_stop_after_start_drains_previously_accepted_events()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        await fixture.Queue.EnqueueAsync(NewEvent("first"), timeout.Token);
        await fixture.Queue.EnqueueAsync(NewEvent("second"), timeout.Token);

        await fixture.Worker.StartAsync(timeout.Token);
        await fixture.Worker.StopAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        Assert.Equal(new[] { "first", "second" }, fixture.State.Attempts.Select(attempt => attempt.Event.EntityId));
        Assert.Equal(new[] { 1, 2 }, fixture.State.DisposedScopes);
        Assert.Empty(fixture.Logger.Entries);
    }

    [Fact]
    public async Task Idle_worker_stops_cleanly_without_resolving_a_writer()
    {
        using var timeout = CreateTimeout();
        await using var fixture = new WorkerFixture();
        await fixture.Worker.StartAsync(timeout.Token);

        await fixture.Worker.StopAsync(timeout.Token);
        await fixture.Worker.ExecuteTask!.WaitAsync(timeout.Token);

        Assert.Empty(fixture.State.Attempts);
        Assert.Equal(0, fixture.State.CreatedScopes);
        Assert.Empty(fixture.Logger.Entries);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public AuditQueue Queue { get; } = new();
        public WriterState State { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public AuditWorker Worker { get; }

        public WorkerFixture()
        {
            var services = new ServiceCollection();
            services.AddScoped<IAuditWriter>(_ => new ScopedWriter(State));
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Worker = new AuditWorker(Queue, _provider.GetRequiredService<IServiceScopeFactory>(), Logger);
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Worker.StopAsync(deadline.Token);
            Worker.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class WriterState
    {
        public int CreatedScopes;
        public ConcurrentQueue<WriteAttempt> Attempts { get; } = new();
        public ConcurrentQueue<int> DisposedScopes { get; } = new();
        public Func<AuditEvent, int, CancellationToken, Task> Write { get; set; } = (_, _, _) => Task.CompletedTask;
    }

    private sealed class ScopedWriter(WriterState state) : IAuditWriter, IAsyncDisposable
    {
        private readonly int _scopeId = Interlocked.Increment(ref state.CreatedScopes);

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            state.Attempts.Enqueue(new WriteAttempt(auditEvent, _scopeId, cancellationToken));
            return state.Write(auditEvent, _scopeId, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            state.DisposedScopes.Enqueue(_scopeId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record WriteAttempt(AuditEvent Event, int ScopeId, CancellationToken Token);
    private sealed record LogEntry(LogLevel Level, Exception? Exception, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<AuditWorker>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public Action<LogEntry>? OnLog { get; set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(item => item.Key, item => item.Value) ?? [];
            var entry = new LogEntry(logLevel, exception, properties);
            Entries.Enqueue(entry);
            OnLog?.Invoke(entry);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    private static AuditEvent NewEvent(string id, AuditEntityType type = AuditEntityType.Claim, string operation = "POST") =>
        new(type, id, operation, new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Local));
}
