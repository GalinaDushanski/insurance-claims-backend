using System.Runtime.InteropServices;
using Claims.Auditing;
using Claims.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;
using Xunit;

namespace Claims.Tests.Integration;

public class AuditPersistenceTests
{
    [Fact]
    public async Task Worker_persists_post_and_delete_as_separate_rows_and_preserves_audit_history()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var sql = new MsSqlBuilder(RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? "mcr.microsoft.com/mssql/server:2022-latest"
            : "mcr.microsoft.com/mssql/server:2019-CU18-ubuntu-20.04").Build();
        await sql.StartAsync(timeout.Token);
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2030, 1, 1, 22, 30, 0, TimeSpan.Zero).AddTicks(1234567),
            TimeZoneInfo.CreateCustomTimeZone("SQL audit tests", TimeSpan.FromHours(3), "SQL audit tests", "SQL audit tests"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<AuditQueue>();
        services.AddSingleton<IAuditer, Auditer>();
        services.AddScoped<IAuditWriter, SqlAuditWriter>();
        services.AddHostedService<AuditWorker>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using (var setup = provider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<AuditContext>().Database.MigrateAsync(timeout.Token);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<AuditWorker>());
        await worker.StartAsync(timeout.Token);
        try
        {
            var auditer = provider.GetRequiredService<IAuditer>();
            const string claimId = "claim-audit-test";
            const string coverId = "cover-audit-test";
            var claimPostTime = clock.GetLocalNow().DateTime;
            await auditer.AuditClaimAsync(claimId, "POST", timeout.Token);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            var claimDeleteTime = clock.GetLocalNow().DateTime;
            await auditer.AuditClaimAsync(claimId, "DELETE", timeout.Token);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            var coverPostTime = clock.GetLocalNow().DateTime;
            await auditer.AuditCoverAsync(coverId, "POST", timeout.Token);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            var coverDeleteTime = clock.GetLocalNow().DateTime;
            await auditer.AuditCoverAsync(coverId, "DELETE", timeout.Token);

            // Drain the accepted events before querying SQL; no polling or sleeps.
            await worker.StopAsync(timeout.Token);
            await worker.ExecuteTask!.WaitAsync(timeout.Token);
            await using var verification = provider.CreateAsyncScope();
            var context = verification.ServiceProvider.GetRequiredService<AuditContext>();
            var claims = await context.ClaimAudits.AsNoTracking().OrderBy(audit => audit.Id).ToListAsync(timeout.Token);
            var covers = await context.CoverAudits.AsNoTracking().OrderBy(audit => audit.Id).ToListAsync(timeout.Token);

            Assert.Equal(2, claims.Count);
            Assert.All(claims, audit => Assert.Equal(claimId, audit.ClaimId));
            Assert.Equal(new[] { "POST", "DELETE" }, claims.Select(audit => audit.HttpRequestType));
            Assert.Equal(new[] { claimPostTime, claimDeleteTime }, claims.Select(audit => audit.Created));
            Assert.NotEqual(claims[0].Id, claims[1].Id);
            Assert.Equal(2, covers.Count);
            Assert.All(covers, audit => Assert.Equal(coverId, audit.CoverId));
            Assert.Equal(new[] { "POST", "DELETE" }, covers.Select(audit => audit.HttpRequestType));
            Assert.Equal(new[] { coverPostTime, coverDeleteTime }, covers.Select(audit => audit.Created));
            Assert.NotEqual(covers[0].Id, covers[1].Id);
            Assert.All(claims, audit => Assert.Equal(DateTimeKind.Unspecified, audit.Created.Kind));
            Assert.All(covers, audit => Assert.Equal(DateTimeKind.Unspecified, audit.Created.Kind));
        }
        finally
        {
            await worker.StopAsync(timeout.Token);
        }
    }
}
