using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Claims.Auditing;
using Claims.Controllers;
using Claims.Repositories;
using Claims.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Claims.Tests.Unit;

// Exercise real MVC binding and response formatting without Program's containers.
public class ControllerResponseTests : IDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public ControllerResponseTests()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddControllers()
                    .AddApplicationPart(typeof(ClaimsController).Assembly)
                    .AddJsonOptions(options =>
                        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                services.AddScoped<IClaimRepository>(_ => new FakeClaimRepository([]));
                services.AddScoped<ICoverRepository>(_ => new FakeCoverRepository([]));
                services.AddScoped<IAuditer>(_ => new RecordingAuditer([]));
                services.AddScoped<IClaimService, ClaimService>();
                services.AddScoped<ICoverService, CoverService>();
                services.AddSingleton<PremiumCalculator>();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }))
            .Start();
        _client = _host.GetTestClient();
    }

    [Theory]
    [InlineData("/Claims/missing")]
    [InlineData("/Covers/missing")]
    public async Task Missing_resource_get_returns_204_with_empty_body(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/Claims/missing")]
    [InlineData("/Covers/missing")]
    public async Task Missing_resource_delete_returns_200_with_empty_body(string path)
    {
        using var response = await _client.DeleteAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/Claims")]
    [InlineData("/Covers")]
    public async Task Empty_list_returns_200_and_json_array(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Claim_create_returns_200_generated_id_and_string_enum()
    {
        var claim = new Claim
        {
            Id = "client-id", CoverId = "cover-id", Name = "Collision",
            Created = new DateTime(2030, 1, 1), Type = ClaimType.Collision, DamageCost = 42m
        };
        using var response = await _client.PostAsJsonAsync("/Claims", claim, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Equal("cover-id", body.GetProperty("coverId").GetString());
        Assert.Equal("Collision", body.GetProperty("name").GetString());
        Assert.Equal(claim.Created, body.GetProperty("created").GetDateTime());
        Assert.Equal("Collision", body.GetProperty("type").GetString());
        Assert.Equal(42m, body.GetProperty("damageCost").GetDecimal());
    }

    [Fact]
    public async Task Cover_create_returns_200_generated_id_and_computed_premium()
    {
        var cover = new Cover
        {
            Id = "client-id", StartDate = new DateTime(2030, 1, 1),
            EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht, Premium = -1m
        };
        using var response = await _client.PostAsJsonAsync("/Covers", cover, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Equal("Yacht", body.GetProperty("type").GetString());
        Assert.Equal(cover.StartDate, body.GetProperty("startDate").GetDateTime());
        Assert.Equal(cover.EndDate, body.GetProperty("endDate").GetDateTime());
        Assert.Equal(3946.25m, body.GetProperty("premium").GetDecimal());
    }

    [Fact]
    public async Task Premium_endpoint_binds_query_parameters_and_returns_200_with_current_total()
    {
        using var response = await _client.PostAsync(
            "/Covers/compute?startDate=2030-01-01&endDate=2030-01-02&coverType=Yacht", null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3946.25m, await response.Content.ReadFromJsonAsync<decimal>(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
