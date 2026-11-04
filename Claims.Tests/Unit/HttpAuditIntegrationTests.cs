using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Claims.Auditing;
using Claims.Controllers;
using Claims.Filters;
using Claims.Repositories;
using Claims.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Claims.Tests.Unit;

public class HttpAuditIntegrationTests
{
    [Theory]
    [InlineData(AuditEntityType.Claim, "POST")]
    [InlineData(AuditEntityType.Claim, "DELETE")]
    [InlineData(AuditEntityType.Cover, "POST")]
    [InlineData(AuditEntityType.Cover, "DELETE")]
    public async Task Http_response_completes_while_sql_writer_is_blocked_and_event_is_processed_after_release(
        AuditEntityType type, string operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var claims = new FakeClaimRepository([]);
        var covers = new FakeCoverRepository([]);
        var cover = new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1),
            EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht
        };
        var claim = new Claim
        {
            Id = "claim-id", CoverId = cover.Id, Name = "Collision", Type = ClaimType.Collision,
            Created = cover.StartDate, DamageCost = 42m
        };
        if (type == AuditEntityType.Claim)
        {
            covers.Items.Add(cover);
            if (operation == "DELETE") claims.Items.Add(claim);
        }
        else if (operation == "DELETE") covers.Items.Add(cover);
        var gate = new WriterGate();
        var clock = new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddControllers(options =>
                    {
                        options.Filters.Add<ValidationExceptionFilter>();
                        options.Filters.Add<ResourceNotFoundExceptionFilter>();
                    })
                    .AddApplicationPart(typeof(ClaimsController).Assembly)
                    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                    services.AddScoped<IClaimRepository>(_ => claims);
                    services.AddScoped<ICoverRepository>(_ => covers);
                    services.AddScoped<IClaimService, ClaimService>();
                    services.AddScoped<ICoverService, CoverService>();
                    services.AddSingleton<PremiumCalculator>();
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton<AuditQueue>();
                    services.AddSingleton<IAuditer, Auditer>();
                    services.AddScoped<IAuditWriter>(_ => new ControlledWriter(gate));
                    services.AddHostedService<AuditWorker>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .Build();
        await host.StartAsync(timeout.Token);
        using var client = host.GetTestClient();
        try
        {
            var path = type == AuditEntityType.Claim ? "/Claims" : "/Covers";
            Task<HttpResponseMessage> request;
            if (operation == "DELETE")
                request = client.DeleteAsync($"{path}/{(type == AuditEntityType.Claim ? claim.Id : cover.Id)}", timeout.Token);
            else if (type == AuditEntityType.Claim)
                request = client.PostAsJsonAsync(path, claim, timeout.Token);
            else
                request = client.PostAsJsonAsync(path, cover, timeout.Token);

            var auditEvent = await gate.Started.Task.WaitAsync(timeout.Token);
            using var response = await request.WaitAsync(timeout.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(gate.Persisted.Task.IsCompleted);
            Assert.False(gate.Release.Task.IsCompleted);
            Assert.Equal(type, auditEvent.EntityType);
            Assert.Equal(operation, auditEvent.HttpRequestType);
            Assert.Equal(clock.GetLocalNow().DateTime, auditEvent.Created);
            if (operation == "POST")
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                Assert.Equal(body.GetProperty("id").GetString(), auditEvent.EntityId);
                Assert.True(Guid.TryParse(auditEvent.EntityId, out _));
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            }
            else
            {
                Assert.Equal(type == AuditEntityType.Claim ? claim.Id : cover.Id, auditEvent.EntityId);
                Assert.Equal("", await response.Content.ReadAsStringAsync(timeout.Token));
            }
            if (type == AuditEntityType.Claim)
            {
                if (operation == "DELETE") Assert.Empty(claims.Items);
                else Assert.Equal(auditEvent.EntityId, Assert.Single(claims.Items).Id);
            }
            else
            {
                if (operation == "DELETE") Assert.Empty(covers.Items);
                else Assert.Equal(auditEvent.EntityId, Assert.Single(covers.Items).Id);
            }

            gate.Release.SetResult();
            Assert.Same(auditEvent, await gate.Persisted.Task.WaitAsync(timeout.Token));
            Assert.Equal(1, gate.WriteCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            await host.StopAsync(timeout.Token);
        }
    }

    private sealed class WriterGate
    {
        public TaskCompletionSource<AuditEvent> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AuditEvent> Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount;
    }

    private sealed class ControlledWriter(WriterGate gate) : IAuditWriter
    {
        public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref gate.WriteCount);
            gate.Started.TrySetResult(auditEvent);
            await gate.Release.Task.WaitAsync(cancellationToken);
            gate.Persisted.TrySetResult(auditEvent);
        }
    }
}
