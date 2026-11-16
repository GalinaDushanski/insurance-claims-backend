using Claims.Domain.Services;
using Claims.Api.Contracts;
using System.Net;
using System.Net.Http.Json;
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

// Exercise real MVC binding and response formatting without Program's containers.
public class ControllerResponseTests : IDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;
    private readonly FakeCoverRepository _covers;
    private readonly List<string> _calls = [];
    private readonly FakeClaimRepository _claims;

    public ControllerResponseTests()
    {
        _claims = new FakeClaimRepository(_calls);
        _covers = new FakeCoverRepository(_calls);
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddControllers(options =>
                {
                    options.Filters.Add<ValidationExceptionFilter>();
                    options.Filters.Add<ResourceNotFoundExceptionFilter>();
                })
                    .AddApplicationPart(typeof(ClaimsController).Assembly)
                    .AddJsonOptions(options =>
                        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                services.AddScoped<IClaimRepository>(_ => _claims);
                services.AddScoped<ICoverRepository>(_ => _covers);
                services.AddScoped<IAuditer>(_ => new RecordingAuditer(_calls));
                services.AddScoped<IClaimService, ClaimService>();
                services.AddScoped<ICoverService, CoverService>();
                services.AddSingleton<IPremiumCalculator, PremiumCalculator>();
                services.AddSingleton<TimeProvider>(
                    new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero)));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_cancellation_reaches_the_inflight_repository_lookup(bool cover)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cover)
        {
            _covers.LookupStarted = started;
            _covers.LookupCompletion = gate;
        }
        else
        {
            _claims.LookupStarted = started;
            _claims.LookupCompletion = gate;
        }

        var request = _client.GetAsync(cover ? "/Covers/existing" : "/Claims/existing", cancellation.Token);
        var repositoryToken = await started.Task.WaitAsync(timeout.Token);
        Assert.True(repositoryToken.CanBeCanceled);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = repositoryToken.Register(() => aborted.TrySetResult());

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await aborted.Task.WaitAsync(timeout.Token);
        Assert.True(repositoryToken.IsCancellationRequested);
        Assert.False(gate.Task.IsCompleted);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims/missing", "Claim")]
    [InlineData("/Covers/missing", "Cover")]
    public async Task Missing_resource_get_returns_404_without_side_effects(string path, string resource)
    {
        SeedResources();
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.Equal("Resource not found.", body.GetProperty("title").GetString());
        Assert.Equal($"{resource} 'missing' was not found.", body.GetProperty("detail").GetString());
        Assert.Single(_claims.Items);
        Assert.Single(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims/missing", "Claim")]
    [InlineData("/Covers/missing", "Cover")]
    public async Task Missing_resource_delete_returns_404_without_auditing_or_deleting(string path, string resource)
    {
        SeedResources();
        using var response = await _client.DeleteAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.Equal("Resource not found.", body.GetProperty("title").GetString());
        Assert.Equal($"{resource} 'missing' was not found.", body.GetProperty("detail").GetString());
        Assert.Single(_claims.Items);
        Assert.Single(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims/existing", "Collision")]
    [InlineData("/Covers/existing", "Yacht")]
    public async Task Existing_resource_get_preserves_200_and_body_without_auditing(string path, string type)
    {
        SeedResources();
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("existing", body.GetProperty("id").GetString());
        Assert.Equal(type, body.GetProperty("type").GetString());
        Assert.Single(_claims.Items);
        Assert.Single(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims/existing", "claim")]
    [InlineData("/Covers/existing", "cover")]
    public async Task Existing_resource_delete_returns_204_publishes_after_deletion_and_returns_404_on_repeat(string path, string resource)
    {
        SeedResources();
        using var response = await _client.DeleteAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "delete-start:existing", "delete-complete:existing", $"audit-{resource}:Deleted:existing" }, _calls);
        if (resource == "claim")
        {
            Assert.Empty(_claims.Items);
            Assert.Single(_covers.Items);
        }
        else
        {
            Assert.Empty(_covers.Items);
            Assert.Single(_claims.Items);
        }

        using var repeated = await _client.DeleteAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.Equal(new[] { "delete-start:existing", "delete-complete:existing", $"audit-{resource}:Deleted:existing" }, _calls);
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
    public async Task Claim_create_returns_201_generated_id_and_string_enum()
    {
        _covers.Items.Add(new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
        });
        var claim = new Claim
        {
            Id = "client-id", CoverId = "cover-id", Name = "Collision",
            Created = new DateTime(2030, 1, 1), Type = ClaimType.Collision, DamageCost = 42m
        };
        using var response = await _client.PostAsJsonAsync("/Claims", claim.ToDto(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Equal("cover-id", body.GetProperty("coverId").GetString());
        Assert.Equal("Collision", body.GetProperty("name").GetString());
        Assert.Equal(claim.Created, body.GetProperty("created").GetDateTime());
        Assert.Equal("Collision", body.GetProperty("type").GetString());
        Assert.Equal(42m, body.GetProperty("damageCost").GetDecimal());
        await AssertCreatedLocationAsync(response, "/Claims", body);
    }

    [Fact]
    public async Task Cover_create_returns_201_generated_id_and_computed_premium()
    {
        var cover = new Cover
        {
            Id = "client-id", StartDate = new DateTime(2030, 1, 1),
            EndDate = new DateTime(2030, 1, 2), Type = CoverType.Yacht, Premium = -1m
        };
        using var response = await _client.PostAsJsonAsync("/Covers", cover.ToDto(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Equal("Yacht", body.GetProperty("type").GetString());
        Assert.Equal(cover.StartDate, body.GetProperty("startDate").GetDateTime());
        Assert.Equal(cover.EndDate, body.GetProperty("endDate").GetDateTime());
        Assert.Equal(1375m, body.GetProperty("premium").GetDecimal());
        await AssertCreatedLocationAsync(response, "/Covers", body);
    }

    [Fact]
    public async Task Premium_endpoint_binds_query_parameters_and_returns_200_with_current_total()
    {
        using var response = await _client.GetAsync(
            "/Covers/compute?startDate=2030-01-01&endDate=2030-01-02&coverType=Yacht",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1375m, await response.Content.ReadFromJsonAsync<decimal>(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Premium_endpoint_rejects_post_without_side_effects()
    {
        using var response = await _client.PostAsync(
            "/Covers/compute?startDate=2030-01-01&endDate=2030-01-02&coverType=Yacht", null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims", 99)]
    [InlineData("/Claims", -1)]
    [InlineData("/Claims", "99")]
    [InlineData("/Covers", 99)]
    [InlineData("/Covers", -1)]
    [InlineData("/Covers", "99")]
    public async Task Undefined_resource_types_return_400_with_field_errors_without_side_effects(string path, object type)
    {
        if (path == "/Claims")
            _covers.Items.Add(new Cover
            {
                Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
            });
        object payload = path == "/Claims"
            ? new
            {
                Id = "client-id", CoverId = "cover-id", Name = "Collision", Created = "2030-01-01",
                Type = type, DamageCost = 42m
            }
            : new
            {
                Id = "client-id", StartDate = "2030-01-01", EndDate = "2030-01-02", Type = type, Premium = -1m
            };
        using var response = await _client.PostAsJsonAsync(path, payload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("errors").GetProperty("type")[0].GetString()));
        Assert.Empty(_claims.Items);
        Assert.Equal(path == "/Claims" ? 1 : 0, _covers.Items.Count);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Undefined")]
    public async Task Invalid_compute_type_returns_400_with_query_field_error_without_side_effects(string type)
    {
        using var response = await _client.GetAsync(
            $"/Covers/compute?startDate=2030-01-01&endDate=2030-01-02&coverType={type}",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("errors").GetProperty("coverType")[0].GetString()));
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims", 0, "Collision")]
    [InlineData("/Claims", 3, "Fire")]
    [InlineData("/Covers", 0, "Yacht")]
    [InlineData("/Covers", 4, "Tanker")]
    public async Task Defined_numeric_resource_types_preserve_201_and_string_enum_response(string path, int type, string expected)
    {
        if (path == "/Claims")
            _covers.Items.Add(new Cover
            {
                Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
            });
        object payload = path == "/Claims"
            ? new
            {
                Id = "client-id", CoverId = "cover-id", Name = "Claim", Created = "2030-01-01",
                Type = type, DamageCost = 100000m
            }
            : new
            {
                Id = "client-id", StartDate = "2030-01-01", EndDate = "2030-01-02", Type = type, Premium = -1m
            };
        using var response = await _client.PostAsJsonAsync(path, payload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expected, body.GetProperty("type").GetString());
        await AssertCreatedLocationAsync(response, path, body);
    }

    [Theory]
    [InlineData(0, "cover-id", "2030-01-01T00:00:00Z", "damageCost")]
    [InlineData(-1, "cover-id", "2030-01-01T00:00:00Z", "damageCost")]
    [InlineData(100001, "cover-id", "2030-01-01T00:00:00Z", "damageCost")]
    [InlineData(42, "missing", "2030-01-01T00:00:00Z", "coverId")]
    [InlineData(42, "cover-id", "2029-12-31T23:59:59Z", "created")]
    [InlineData(42, "cover-id", "2030-01-02T00:00:01Z", "created")]
    [InlineData(42, "cover-id", "2030-01-01T01:00:00+02:00", "created")]
    [InlineData(42, "cover-id", "2030-01-01T23:30:00-02:00", "created")]
    public async Task Invalid_claim_returns_400_with_field_errors_and_no_side_effects(
        int cost, string coverId, string created, string field)
    {
        _covers.Items.Add(new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
        });
        using var response = await _client.PostAsJsonAsync("/Claims", new
        {
            Id = "client-id", CoverId = coverId, Name = "Collision",
            Created = created, Type = "Collision", DamageCost = cost
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("One or more validation errors occurred.", body.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("errors").GetProperty(field)[0].GetString()));
        Assert.Empty(_claims.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("2030-01-01T00:00:00Z")]
    [InlineData("2030-01-01T02:00:00+02:00")]
    [InlineData("2029-12-31T19:00:00-05:00")]
    [InlineData("2030-01-01T00:00:00")]
    [InlineData("2030-01-01")]
    public async Task Claim_timestamps_with_offsets_or_without_timezone_save_the_same_utc_instant(string created)
    {
        _covers.Items.Add(new Cover
        {
            Id = "cover-id", StartDate = new DateTime(2030, 1, 1), EndDate = new DateTime(2030, 1, 2)
        });
        using var response = await _client.PostAsJsonAsync("/Claims", new
        {
            Id = "client-id", CoverId = "cover-id", Name = "Collision",
            Created = created, Type = "Collision", DamageCost = 42m
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = Assert.Single(_claims.Items);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), saved.Created);
        Assert.Equal(DateTimeKind.Utc, saved.Created.Kind);
        Assert.Equal("2030-01-01T00:00:00Z", body.GetProperty("created").GetString());
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData(123)]
    [InlineData(null)]
    public async Task Malformed_claim_timestamp_returns_400_without_persisting_or_auditing(object? created)
    {
        using var response = await _client.PostAsJsonAsync("/Claims", new
        {
            Id = "client-id", CoverId = "cover-id", Name = "Collision",
            Created = created, Type = "Collision", DamageCost = 42m
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.NotEmpty(body.GetProperty("errors").EnumerateObject());
        Assert.Empty(_claims.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(false, "2029-12-31", "2030-01-01", "startDate")]
    [InlineData(true, "2029-12-31", "2030-01-01", "startDate")]
    [InlineData(false, "2030-01-01T00:30:00+02:00", "2030-01-02T00:30:00+02:00", "startDate")]
    [InlineData(true, "2030-01-01T00:30:00+02:00", "2030-01-02T00:30:00+02:00", "startDate")]
    [InlineData(false, "2030-01-01", "2029-12-31", "endDate")]
    [InlineData(true, "2030-01-01", "2029-12-31", "endDate")]
    [InlineData(false, "2030-01-01", "2030-01-01", "endDate")]
    [InlineData(true, "2030-01-01", "2030-01-01", "endDate")]
    [InlineData(false, "2030-01-01", "2031-01-01T00:00:00.0000001Z", "endDate")]
    [InlineData(true, "2030-01-01", "2031-01-01T00:00:00.0000001Z", "endDate")]
    public async Task Invalid_cover_create_and_compute_return_400_without_side_effects(
        bool compute, string start, string end, string field)
    {
        using var response = await SendCoverAsync(compute, start, end);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("errors").GetProperty(field)[0].GetString()));
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("2030-01-01T02:00:00+02:00", "2030-01-02T02:00:00+02:00")]
    [InlineData("2029-12-31T19:00:00-05:00", "2030-01-01T19:00:00-05:00")]
    [InlineData("2030-01-01T00:00:00", "2030-01-02T00:00:00")]
    [InlineData("2030-01-01", "2030-01-02")]
    public async Task Cover_create_and_compute_normalize_dates_and_preserve_current_premium(string start, string end)
    {
        using var quote = await SendCoverAsync(true, start, end);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Equal(1375m, await quote.Content.ReadFromJsonAsync<decimal>(TestContext.Current.CancellationToken));
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);

        using var creation = await SendCoverAsync(false, start, end);
        var body = await creation.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var saved = Assert.Single(_covers.Items);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), saved.StartDate);
        Assert.Equal(new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc), saved.EndDate);
        Assert.Equal(DateTimeKind.Utc, saved.StartDate.Kind);
        Assert.Equal(DateTimeKind.Utc, saved.EndDate.Kind);
        Assert.Equal(1375m, saved.Premium);
        Assert.Equal("2030-01-01T00:00:00Z", body.GetProperty("startDate").GetString());
        Assert.Equal("2030-01-02T00:00:00Z", body.GetProperty("endDate").GetString());
        Assert.Equal(new[] { "save-start", "save-complete", $"audit-cover:Created:{saved.Id}" }, _calls);
    }

    [Theory]
    [InlineData("/Covers/compute?startDate=not-a-date&endDate=2030-01-02&coverType=Yacht", "startDate")]
    [InlineData("/Covers/compute?startDate=2030-01-01&coverType=Yacht", "endDate")]
    public async Task Malformed_or_missing_compute_dates_return_400(string path, string field)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("startDate", "not-a-date")]
    [InlineData("startDate", 123)]
    [InlineData("startDate", null)]
    [InlineData("endDate", "not-a-date")]
    [InlineData("endDate", 123)]
    [InlineData("endDate", null)]
    public async Task Malformed_cover_json_date_returns_400_without_side_effects(string field, object? value)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "client-id", ["type"] = "Yacht", ["premium"] = -1m,
            ["startDate"] = "2030-01-01", ["endDate"] = "2030-01-02"
        };
        payload[field] = value;
        using var response = await _client.PostAsJsonAsync("/Covers", payload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains(body.GetProperty("errors").EnumerateObject(),
            error => error.Name.Contains(field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Blank_claim_cover_id_returns_400_without_side_effects(string? coverId)
    {
        using var response = await _client.PostAsJsonAsync("/Claims", new
        {
            Id = "client-id", CoverId = coverId, Name = "Collision", Created = "2030-01-01",
            Type = "Collision", DamageCost = 42m
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(body.GetProperty("errors").EnumerateObject(),
            error => error.Name.Equals("coverId", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_claims.Items);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("/Claims")]
    [InlineData("/Covers")]
    public async Task Existing_model_contract_requires_id_in_create_body(string path)
    {
        using var response = await _client.PostAsJsonAsync(path, new
        {
            CoverId = "cover-id", Name = "Collision", Created = "2030-01-01",
            StartDate = "2030-01-01", EndDate = "2030-01-02", Type = 0, DamageCost = 42m
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(body.GetProperty("errors").EnumerateObject(),
            error => error.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_claims.Items);
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
    }

    private async Task AssertCreatedLocationAsync(
        HttpResponseMessage response, string path, System.Text.Json.JsonElement created)
    {
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.True(location.IsAbsoluteUri);
        Assert.Equal($"{path}/{created.GetProperty("id").GetString()}", location.AbsolutePath);
        using var get = await _client.GetAsync(location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(created.ToString(), body.ToString());
    }

    private Task<HttpResponseMessage> SendCoverAsync(bool compute, string start, string end)
    {
        if (compute)
            return _client.GetAsync(
                $"/Covers/compute?startDate={Uri.EscapeDataString(start)}&endDate={Uri.EscapeDataString(end)}&coverType=Yacht",
                TestContext.Current.CancellationToken);

        return _client.PostAsJsonAsync("/Covers", new
        {
            Id = "client-id", StartDate = start, EndDate = end, Type = "Yacht", Premium = -1m
        }, TestContext.Current.CancellationToken);
    }

    private void SeedResources()
    {
        _claims.Items.Add(new Claim
        {
            Id = "existing", CoverId = "existing", Name = "Collision", Type = ClaimType.Collision,
            Created = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), DamageCost = 42m
        });
        _covers.Items.Add(new Cover
        {
            Id = "existing", Type = CoverType.Yacht, Premium = 1375m,
            StartDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
