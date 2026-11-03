using System.Globalization;
using Claims.Services;
using Claims.Validation;
using Xunit;

namespace Claims.Tests.Unit;

public class ClaimValidationTests
{
    private static readonly DateTime Start = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddDays(1);
    private readonly List<string> _calls = [];
    private readonly FakeClaimRepository _claims;
    private readonly FakeCoverRepository _covers = new([]);
    private readonly ClaimService _service;

    public ClaimValidationTests()
    {
        _claims = new FakeClaimRepository(_calls);
        _covers.Items.Add(new Cover { Id = "cover-id", StartDate = Start, EndDate = End });
        _service = new ClaimService(_claims, new RecordingAuditer(_calls), _covers);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("99999.99")]
    [InlineData("100000")]
    public async Task Costs_within_inclusive_limits_are_accepted(string cost)
    {
        var claim = NewClaim();
        claim.DamageCost = decimal.Parse(cost, CultureInfo.InvariantCulture);

        var result = await _service.CreateAsync(claim);

        Assert.Equal(claim.DamageCost, result.DamageCost);
        Assert.Same(claim, Assert.Single(_claims.Items));
        Assert.Equal(new[] { "save-start", "save-complete", $"audit-claim:POST:{claim.Id}" }, _calls);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("100000.01")]
    public async Task Costs_outside_limits_are_rejected_without_side_effects(string cost)
    {
        var claim = NewClaim();
        claim.DamageCost = decimal.Parse(cost, CultureInfo.InvariantCulture);

        await AssertRejectedAsync(claim, "damageCost");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("missing")]
    public async Task Missing_blank_or_unknown_cover_id_is_rejected_without_side_effects(string? coverId)
    {
        var claim = NewClaim();
        claim.CoverId = coverId!;

        await AssertRejectedAsync(claim, "coverId");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(TimeSpan.TicksPerDay)]
    [InlineData(TimeSpan.TicksPerHour)]
    public async Task Claim_at_either_boundary_or_inside_cover_is_accepted(long ticksAfterStart)
    {
        var claim = NewClaim();
        claim.Created = Start.AddTicks(ticksAfterStart);

        await _service.CreateAsync(claim);

        Assert.Same(claim, Assert.Single(_claims.Items));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(TimeSpan.TicksPerDay + 1)]
    public async Task Claim_one_tick_outside_cover_is_rejected_without_side_effects(long ticksAfterStart)
    {
        var claim = NewClaim();
        claim.Created = Start.AddTicks(ticksAfterStart);

        await AssertRejectedAsync(claim, "created");
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Utc_and_unspecified_dates_compare_consistently_and_save_as_utc(DateTimeKind kind)
    {
        var cover = Assert.Single(_covers.Items);
        cover.StartDate = DateTime.SpecifyKind(Start, kind);
        cover.EndDate = DateTime.SpecifyKind(End, kind);
        var claim = NewClaim();
        claim.Created = DateTime.SpecifyKind(Start, kind);

        await _service.CreateAsync(claim);

        Assert.Equal(Start, claim.Created);
        Assert.Equal(DateTimeKind.Utc, claim.Created.Kind);
        Assert.Equal(kind, cover.StartDate.Kind);
        Assert.Equal(kind, cover.EndDate.Kind);
    }

    [Fact]
    public async Task Ambiguous_local_claim_date_is_rejected_without_using_server_timezone()
    {
        var claim = NewClaim();
        claim.Created = DateTime.SpecifyKind(Start, DateTimeKind.Local);

        await AssertRejectedAsync(claim, "created");
    }

    [Fact]
    public async Task Ambiguous_local_cover_date_is_rejected_without_using_server_timezone()
    {
        Assert.Single(_covers.Items).StartDate = DateTime.SpecifyKind(Start, DateTimeKind.Local);

        await AssertRejectedAsync(NewClaim(), "coverId");
    }

    [Fact]
    public async Task Independent_errors_are_reported_together()
    {
        var claim = NewClaim();
        claim.DamageCost = -1m;
        claim.CoverId = "missing";

        var error = await AssertRejectedAsync(claim, "damageCost");

        Assert.Contains("coverId", error.Errors.Keys);
    }

    private async Task<BusinessValidationException> AssertRejectedAsync(Claim claim, string field)
    {
        var originalId = claim.Id;
        var originalCreated = claim.Created;
        var error = await Assert.ThrowsAsync<BusinessValidationException>(() => _service.CreateAsync(claim));

        Assert.Contains(field, error.Errors.Keys);
        Assert.All(error.Errors.Values, messages => Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message))));
        Assert.Equal(originalId, claim.Id);
        Assert.Equal(originalCreated, claim.Created);
        Assert.Equal(originalCreated.Kind, claim.Created.Kind);
        Assert.Empty(_claims.Items);
        Assert.Empty(_calls);
        return error;
    }

    private static Claim NewClaim() => new()
    {
        Id = "client-id", CoverId = "cover-id", Name = "Collision",
        Created = Start, Type = ClaimType.Collision, DamageCost = 42m
    };
}
