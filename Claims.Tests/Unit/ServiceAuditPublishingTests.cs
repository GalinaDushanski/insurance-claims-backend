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
                    requestOperation = operation == "POST" ? service.CreateAsync(claim) : service.DeleteAsync(claim.Id);
                }
                else
                {
                    var service = requestScope.ServiceProvider.GetRequiredService<ICoverService>();
                    requestOperation = operation == "POST" ? service.CreateAsync(cover) : service.DeleteAsync(cover.Id);
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
            requestOperation = operation == "POST" ? service.CreateAsync(claim) : service.DeleteAsync(claim.Id);
        }
        else
        {
            var service = new CoverService(covers, auditer, new PremiumCalculator(), NewClock());
            if (operation == "POST") covers.AddFailure = failure;
            else covers.DeleteFailure = failure;
            requestOperation = operation == "POST" ? service.CreateAsync(NewCover()) : service.DeleteAsync(cover.Id);
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

        Assert.Single(await claimService.GetAllAsync());
        await claimService.GetByIdAsync("claim-id");
        Assert.Single(await coverService.GetAllAsync());
        var cover = await coverService.GetByIdAsync("cover-id");
        Assert.Equal(3946.25m, coverService.ComputePremium(cover.StartDate, cover.EndDate, cover.Type));

        queue.Complete();
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.False(await reader.MoveNextAsync());
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
