using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Claims.Auditing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Claims.Tests;

public class ClaimsControllerTests
{
    [Fact]
    public async Task Production_host_preserves_crud_swagger_and_eventual_audit_history()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        using var list = await client.GetAsync("/Claims", token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("[]", await list.Content.ReadAsStringAsync(token));
        using var swagger = await client.GetAsync("/swagger/v1/swagger.json", token);
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        using var api = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync(token));
        Assert.Equal(new[] { "/Claims", "/Claims/{id}", "/Covers", "/Covers/compute", "/Covers/{id}" }.Order(),
            api.RootElement.GetProperty("paths").EnumerateObject().Select(x => x.Name).Order());
        var schemas = api.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("Claim", out _));
        Assert.True(schemas.TryGetProperty("Cover", out _));
        var computeParameters = api.RootElement.GetProperty("paths").GetProperty("/Covers/compute")
            .GetProperty("post").GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(new[] { "coverType", "endDate", "startDate" },
            computeParameters.Select(x => x.GetProperty("name").GetString()).Order());
        Assert.All(computeParameters, p => Assert.Equal("query", p.GetProperty("in").GetString()));

        var start = DateTime.UtcNow.Date.AddDays(1);
        var end = start.AddDays(200);
        var quotePath = $"/Covers/compute?startDate={start:yyyy-MM-dd}&endDate={end:yyyy-MM-dd}&coverType=Yacht";
        using var quote = await client.PostAsync(quotePath, null, token);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Equal(262487.50m, await quote.Content.ReadFromJsonAsync<decimal>(token));
        using var coverResponse = await client.PostAsJsonAsync("/Covers", new
        {
            id = "client-cover", startDate = start, endDate = end, type = "Yacht", premium = -1m
        }, token);
        Assert.Equal(HttpStatusCode.OK, coverResponse.StatusCode);
        var cover = await coverResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var coverId = cover.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(coverId, out _));
        Assert.Equal(262487.50m, cover.GetProperty("premium").GetDecimal());
        using var coverGet = await client.GetAsync($"/Covers/{coverId}", token);
        Assert.Equal(HttpStatusCode.OK, coverGet.StatusCode);
        var reloadedCover = await coverGet.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(coverId, reloadedCover.GetProperty("id").GetString());
        Assert.Equal(start, reloadedCover.GetProperty("startDate").GetDateTime());
        Assert.Equal("Yacht", reloadedCover.GetProperty("type").GetString());

        using var invalid = await client.PostAsJsonAsync("/Claims", new
        {
            id = "invalid", coverId, created = start, name = "Invalid claim", type = "Collision", damageCost = 100001m
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("DamageCost must be between 0 and 100000, inclusive.",
            error.GetProperty("errors").GetProperty("damageCost")[0].GetString());

        using var claimResponse = await client.PostAsJsonAsync("/Claims", new
        {
            id = "client-claim", coverId, created = start.AddDays(1), name = "Smoke claim", type = "Collision", damageCost = 42m
        }, token);
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var claim = await claimResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var claimId = claim.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(claimId, out _));
        Assert.Equal(coverId, claim.GetProperty("coverId").GetString());
        using var claimGet = await client.GetAsync($"/Claims/{claimId}", token);
        Assert.Equal(HttpStatusCode.OK, claimGet.StatusCode);
        var reloadedClaim = await claimGet.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(claimId, reloadedClaim.GetProperty("id").GetString());
        Assert.Equal("Collision", reloadedClaim.GetProperty("type").GetString());
        Assert.Equal(42m, reloadedClaim.GetProperty("damageCost").GetDecimal());
        foreach (var path in new[] { $"/Claims/{claimId}", $"/Covers/{coverId}" })
        {
            using var deletion = await client.DeleteAsync(path, token);
            Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
            Assert.Equal("", await deletion.Content.ReadAsStringAsync(token));
            using var missing = await client.GetAsync(path, token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var repeated = await client.DeleteAsync(path, token);
            Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        }

        // Query fresh scopes until asynchronous persistence catches up; HTTP completion is not SQL completion.
        using var auditTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        auditTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AuditContext>();
            var claims = await context.ClaimAudits.AsNoTracking().OrderBy(x => x.Id).ToListAsync(auditTimeout.Token);
            var covers = await context.CoverAudits.AsNoTracking().OrderBy(x => x.Id).ToListAsync(auditTimeout.Token);
            if (claims.Count >= 2 && covers.Count >= 2)
            {
                Assert.Equal(new[] { "POST", "DELETE" }, claims.Select(x => x.HttpRequestType));
                Assert.Equal(new[] { "POST", "DELETE" }, covers.Select(x => x.HttpRequestType));
                Assert.All(claims, x => Assert.Equal(claimId, x.ClaimId));
                Assert.All(covers, x => Assert.Equal(coverId, x.CoverId));
                break;
            }
            await Task.Delay(100, auditTimeout.Token);
        }
    }
}
