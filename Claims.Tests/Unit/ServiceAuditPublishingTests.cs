using Claims.Auditing;
using Claims.Repositories;
using Claims.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Claims.Tests.Unit;

public class ServiceAuditPublishingTests
{
    [Theory]
    [InlineData(AuditEntityType.Claim, "POST")]
    [InlineData(AuditEntityType.Claim, "DELETE")]
    [InlineData(AuditEntityType.Cover, "POST")]
    [InlineData(AuditEntityType.Cover, "DELETE")]
    public async Task Operation_completes_after_enqueue_while_worker_is_still_waiting_for_sql(
        AuditEntityType type, string operation)
    {
        using var timeout = CreateTimeout();
        var calls = new List<string>();
        var claims = new FakeClaimRepository(calls);
        var covers = new FakeCoverRepository(calls);
        var clock = NewClock();
        var claim = NewClaim();
        var cover = NewCover();
        if (type == AuditEntityType.Claim)
        {
            covers.Items.Add(cover);
            if (operation == "DELETE") claims.Items.Add(claim);
        }
        else if (operation == "DELETE") covers.Items.Add(cover);
        var gate = new WriterGate();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AuditQueue>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IAuditer, Auditer>();
        services.AddScoped<IAuditWriter>(_ => new ControlledWriter(gate));
        services.AddHostedService<AuditWorker>();
        services.AddScoped<IClaimRepository>(_ => claims);
        services.AddScoped<ICoverRepository>(_ => covers);
        services.AddScoped<IClaimService, ClaimService>();
        services.AddScoped<ICoverService, CoverService>();
        services.AddSingleton<PremiumCalculator>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<AuditWorker>());
        await worker.StartAsync(timeout.Token);
        try
        {
            await using (var requestScope = provider.CreateAsyncScope())
            {
                Task requestOperation;
                if (type == AuditEntityType.Claim)
                {
                    var service = requestScope.ServiceProvider.GetRequiredService<IClaimService>();
                    requestOperation = operation == "POST" ? service.CreateAsync(claim, timeout.Token) : service.DeleteAsync(claim.Id, timeout.Token);
                }
                else
                {
                    var service = requestScope.ServiceProvider.GetRequiredService<ICoverService>();
                    requestOperation = operation == "POST" ? service.CreateAsync(cover, timeout.Token) : service.DeleteAsync(cover.Id, timeout.Token);
                }

                var auditEvent = await gate.Started.Task.WaitAsync(timeout.Token);
                await requestOperation.WaitAsync(timeout.Token);

                Assert.False(gate.Persisted.Task.IsCompleted);
                Assert.Equal(type, auditEvent.EntityType);
                Assert.Equal(type == AuditEntityType.Claim ? claim.Id : cover.Id, auditEvent.EntityId);
                Assert.Equal(operation, auditEvent.HttpRequestType);
                Assert.Equal(clock.GetLocalNow().DateTime, auditEvent.Created);
                if (type == AuditEntityType.Claim)
                {
                    if (operation == "DELETE") Assert.Empty(claims.Items);
                    else Assert.Same(claim, Assert.Single(claims.Items));
                }
                else
                {
                    if (operation == "DELETE") Assert.Empty(covers.Items);
                    else Assert.Same(cover, Assert.Single(covers.Items));
                }
            }

            gate.Release.SetResult();
            await gate.Persisted.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, gate.WriteCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            await worker.StopAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData(AuditEntityType.Claim, "POST")]
    [InlineData(AuditEntityType.Claim, "DELETE")]
    [InlineData(AuditEntityType.Cover, "POST")]
    [InlineData(AuditEntityType.Cover, "DELETE")]
    public async Task Failed_database_operations_do_not_enqueue_events(AuditEntityType type, string operation)
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var auditer = new Auditer(queue, NewClock());
        var claims = new FakeClaimRepository([]);
        var covers = new FakeCoverRepository([]);
        var claim = NewClaim();
        var cover = NewCover();
        covers.Items.Add(cover);
        var failure = new InvalidOperationException("MongoDB operation failed");
        Task requestOperation;
        if (type == AuditEntityType.Claim)
        {
            var service = new ClaimService(claims, auditer, covers);
            if (operation == "POST") claims.AddFailure = failure;
            else
            {
                claims.Items.Add(claim);
                claims.DeleteFailure = failure;
            }
            requestOperation = operation == "POST" ? service.CreateAsync(claim, timeout.Token) : service.DeleteAsync(claim.Id, timeout.Token);
        }
        else
        {
            var service = new CoverService(covers, auditer, new PremiumCalculator(), NewClock());
            if (operation == "POST") covers.AddFailure = failure;
            else covers.DeleteFailure = failure;
            requestOperation = operation == "POST" ? service.CreateAsync(NewCover(), timeout.Token) : service.DeleteAsync(cover.Id, timeout.Token);
        }

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => requestOperation));
        queue.Complete();
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Reads_and_premium_computation_do_not_enqueue_events()
    {
        using var timeout = CreateTimeout();
        var queue = new AuditQueue();
        var auditer = new Auditer(queue, NewClock());
        var claims = new FakeClaimRepository([]);
        var covers = new FakeCoverRepository([]);
        claims.Items.Add(NewClaim());
        covers.Items.Add(NewCover());
        var claimService = new ClaimService(claims, auditer, covers);
        var coverService = new CoverService(covers, auditer, new PremiumCalculator(), NewClock());

        Assert.Single(await claimService.GetAllAsync(timeout.Token));
        await claimService.GetByIdAsync("claim-id", timeout.Token);
        Assert.Single(await coverService.GetAllAsync(timeout.Token));
        var cover = await coverService.GetByIdAsync("cover-id", timeout.Token);
        Assert.Equal(1375m, coverService.ComputePremium(cover.StartDate, cover.EndDate, cover.Type, timeout.Token));

        queue.Complete();
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.False(await reader.MoveNextAsync());
    }

    [Theory]
    [InlineData(AuditEntityType.Claim, "POST", "before-enqueue")]
    [InlineData(AuditEntityType.Claim, "DELETE", "before-enqueue")]
    [InlineData(AuditEntityType.Cover, "POST", "before-enqueue")]
    [InlineData(AuditEntityType.Cover, "DELETE", "before-enqueue")]
    [InlineData(AuditEntityType.Claim, "POST", "during-enqueue")]
    [InlineData(AuditEntityType.Claim, "DELETE", "during-enqueue")]
    [InlineData(AuditEntityType.Cover, "POST", "during-enqueue")]
    [InlineData(AuditEntityType.Cover, "DELETE", "during-enqueue")]
    [InlineData(AuditEntityType.Claim, "POST", "timeout")]
    [InlineData(AuditEntityType.Claim, "DELETE", "timeout")]
    [InlineData(AuditEntityType.Cover, "POST", "timeout")]
    [InlineData(AuditEntityType.Cover, "DELETE", "timeout")]
    public async Task Persisted_mutation_enqueues_despite_client_cancellation_or_reports_independent_timeout(
        AuditEntityType type, string operation, string cancellationStage)
    {
        using var timeout = CreateTimeout();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var queue = new AuditQueue();
        var filler = new AuditEvent(AuditEntityType.Claim, "buffered", "POST", NewClock().GetLocalNow().DateTime);
        for (var index = 0; index < 1000; index++)
            await queue.EnqueueAsync(filler, timeout.Token);

        var claims = new FakeClaimRepository([]);
        var covers = new FakeCoverRepository([]);
        var claim = NewClaim();
        var cover = NewCover();
        if (type == AuditEntityType.Claim) covers.Items.Add(cover);
        if (operation == "DELETE")
        {
            if (type == AuditEntityType.Claim) claims.Items.Add(claim);
            else covers.Items.Add(cover);
        }
        // Cancel precisely after the mutation, before control returns to the service.
        if (cancellationStage == "before-enqueue")
        {
            if (type == AuditEntityType.Claim) claims.AfterPersistence = requestCancellation.Cancel;
            else covers.AfterPersistence = requestCancellation.Cancel;
        }
        var auditer = new Auditer(queue, NewClock());
        Task mutation;
        if (type == AuditEntityType.Claim)
        {
            var service = new ClaimService(claims, auditer, covers);
            mutation = operation == "POST"
                ? service.CreateAsync(claim, requestCancellation.Token)
                : service.DeleteAsync(claim.Id, requestCancellation.Token);
        }
        else
        {
            var service = new CoverService(covers, auditer, new PremiumCalculator(), NewClock());
            mutation = operation == "POST"
                ? service.CreateAsync(cover, requestCancellation.Token)
                : service.DeleteAsync(cover.Id, requestCancellation.Token);
        }

        Assert.False(mutation.IsCompleted);
        if (type == AuditEntityType.Claim)
        {
            if (operation == "DELETE") Assert.Empty(claims.Items);
            else Assert.Same(claim, Assert.Single(claims.Items));
        }
        else
        {
            if (operation == "DELETE") Assert.Empty(covers.Items);
            else Assert.Same(cover, Assert.Single(covers.Items));
        }
        Assert.All(claims.Tokens.Concat(covers.Tokens), token => Assert.Equal(requestCancellation.Token, token));
        await requestCancellation.CancelAsync();

        if (cancellationStage == "timeout")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(timeout.Token));
            Assert.NotEqual(requestCancellation.Token, error.CancellationToken);
            Assert.NotEqual(timeout.Token, error.CancellationToken);
            Assert.True(error.CancellationToken.IsCancellationRequested);
            queue.Complete();
            var count = 0;
            await foreach (var item in queue.ReadAllAsync(timeout.Token))
            {
                Assert.Same(filler, item);
                count++;
            }
            Assert.Equal(1000, count);
        }
        else
        {
            // Free capacity only after the request has been cancelled.
            await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await reader.MoveNextAsync());
            Assert.Same(filler, reader.Current);
            await mutation.WaitAsync(timeout.Token);
            queue.Complete();
            var published = new List<AuditEvent>();
            while (await reader.MoveNextAsync())
                if (reader.Current.EntityId != "buffered") published.Add(reader.Current);
            var auditEvent = Assert.Single(published);
            Assert.Equal(type, auditEvent.EntityType);
            Assert.Equal(operation, auditEvent.HttpRequestType);
            Assert.Equal(type == AuditEntityType.Claim ? claim.Id : cover.Id, auditEvent.EntityId);
        }
    }

    private sealed class WriterGate
    {
        public TaskCompletionSource<AuditEvent> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount;
    }

    private sealed class ControlledWriter(WriterGate gate) : IAuditWriter
    {
        public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref gate.WriteCount);
            gate.Started.TrySetResult(auditEvent);
            await gate.Release.Task.WaitAsync(cancellationToken);
            gate.Persisted.TrySetResult();
        }
    }

    private static Claim NewClaim() => new()
    {
        Id = "claim-id", CoverId = "cover-id", Created = new DateTime(2030, 1, 1),
        Name = "Collision", Type = ClaimType.Collision, DamageCost = 42m
    };

    private static Cover NewCover() => new()
    {
        Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht
    };

    private static FixedTimeProvider NewClock() => new(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }
}
