using System.Globalization;
using Claims.Services;
using Claims.Validation;
using Xunit;

namespace Claims.Tests.Unit;

public class CoverValidationTests
{
    private static readonly DateTime Today = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _calls = [];
    private readonly FakeCoverRepository _covers;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(Today.AddHours(12)));
    private readonly CoverService _service;

    public CoverValidationTests()
    {
        _covers = new FakeCoverRepository(_calls);
        _service = new CoverService(_covers, new RecordingAuditer(_calls), new PremiumCalculator(), _clock);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Start_date_uses_today_in_utc_including_today_and_tomorrow(int daysFromToday)
    {
        var cover = NewCover(Today.AddDays(daysFromToday), Today.AddDays(daysFromToday + 1));
        if (daysFromToday < 0)
        {
            await AssertRejectedAsync(cover, "startDate");
            return;
        }

        Assert.Equal(3946.25m, _service.ComputePremium(cover.StartDate, cover.EndDate, cover.Type));
        await _service.CreateAsync(cover);
        Assert.Same(cover, Assert.Single(_covers.Items));
        Assert.Equal(3946.25m, cover.Premium);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(23)]
    public async Task Starting_today_is_valid_regardless_of_time_of_day(int hour)
    {
        _clock.UtcNow = new DateTimeOffset(Today.AddHours(23).AddMinutes(59));
        var start = Today.AddHours(hour);

        await _service.CreateAsync(NewCover(start, start.AddDays(1)));

        Assert.Single(_covers.Items);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public async Task End_date_before_or_equal_to_start_is_rejected(long ticksAfterStart)
    {
        await AssertRejectedAsync(NewCover(Today, Today.AddTicks(ticksAfterStart)), "endDate");
    }

    [Fact]
    public async Task End_date_one_tick_after_start_is_valid_for_creation_and_premium_computation()
    {
        var cover = NewCover(Today, Today.AddTicks(1));

        var quotedPremium = _service.ComputePremium(cover.StartDate, cover.EndDate, cover.Type);

        Assert.Equal(3946.25m, quotedPremium);
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);

        var result = await _service.CreateAsync(cover);

        Assert.Same(cover, result);
        Assert.Same(cover, Assert.Single(_covers.Items));
        Assert.Equal(Today, result.StartDate);
        Assert.Equal(Today.AddTicks(1), result.EndDate);
        Assert.Equal(quotedPremium, result.Premium);
        Assert.Equal(new[] { "save-start", "save-complete", $"audit-cover:POST:{cover.Id}" }, _calls);
    }

    [Theory]
    [InlineData("2030-01-01T12:00:00Z", "2031-01-01T12:00:00Z", true)]
    [InlineData("2030-01-01T12:00:00Z", "2031-01-01T12:00:00.0000001Z", false)]
    [InlineData("2031-03-01T12:00:00Z", "2032-03-01T12:00:00Z", true)]
    [InlineData("2031-03-01T12:00:00Z", "2032-03-01T12:00:00.0000001Z", false)]
    [InlineData("2032-02-29T12:00:00Z", "2033-02-28T12:00:00Z", true)]
    [InlineData("2032-02-29T12:00:00Z", "2033-02-28T12:00:00.0000001Z", false)]
    [InlineData("2031-02-28T12:00:00Z", "2032-02-28T12:00:00Z", true)]
    [InlineData("2031-02-28T12:00:00Z", "2032-02-29T12:00:00Z", false)]
    public async Task Calendar_year_limit_preserves_time_and_handles_leap_years(string start, string end, bool valid)
    {
        var cover = NewCover(ParseUtc(start), ParseUtc(end));
        if (!valid)
        {
            await AssertRejectedAsync(cover, "endDate");
            return;
        }

        var quotedPremium = _service.ComputePremium(cover.StartDate, cover.EndDate, cover.Type);
        await _service.CreateAsync(cover);

        Assert.Same(cover, Assert.Single(_covers.Items));
        Assert.Equal(quotedPremium, cover.Premium);
    }

    [Fact]
    public async Task Starting_yesterday_becomes_invalid_exactly_at_utc_midnight()
    {
        var cover = NewCover(Today, Today.AddDays(1));
        _clock.UtcNow = new DateTimeOffset(Today.AddDays(1).AddTicks(-1));
        Assert.Equal(3946.25m, _service.ComputePremium(cover.StartDate, cover.EndDate, cover.Type));

        _clock.UtcNow = new DateTimeOffset(Today.AddDays(1));

        await AssertRejectedAsync(cover, "startDate");
    }

    [Fact]
    public async Task Today_comes_from_the_utc_date_not_the_clock_offsets_local_date()
    {
        _clock.UtcNow = new DateTimeOffset(2029, 12, 31, 19, 0, 0, TimeSpan.FromHours(-5));

        await AssertRejectedAsync(NewCover(Today.AddDays(-1), Today), "startDate");
        await _service.CreateAsync(NewCover(Today, Today.AddDays(1)));

        Assert.Single(_covers.Items);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Accepted_dates_are_normalized_to_utc_before_saving(DateTimeKind kind)
    {
        var cover = NewCover(DateTime.SpecifyKind(Today, kind), DateTime.SpecifyKind(Today.AddDays(1), kind));

        await _service.CreateAsync(cover);

        Assert.Equal(DateTimeKind.Utc, cover.StartDate.Kind);
        Assert.Equal(DateTimeKind.Utc, cover.EndDate.Kind);
        Assert.Equal(Today, cover.StartDate);
        Assert.Equal(Today.AddDays(1), cover.EndDate);
        Assert.Equal(3946.25m, cover.Premium);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ambiguous_local_dates_are_rejected_without_side_effects(bool localStart)
    {
        var cover = NewCover(Today, Today.AddDays(1));
        if (localStart) cover.StartDate = DateTime.SpecifyKind(cover.StartDate, DateTimeKind.Local);
        else cover.EndDate = DateTime.SpecifyKind(cover.EndDate, DateTimeKind.Local);

        await AssertRejectedAsync(cover, localStart ? "startDate" : "endDate");
    }

    [Fact]
    public async Task Invalid_creation_reports_multiple_errors_and_preserves_all_submitted_values()
    {
        var error = await AssertRejectedAsync(NewCover(Today.AddDays(-1), Today.AddDays(-2)), "startDate");

        Assert.Contains("endDate", error.Errors.Keys);
    }

    [Fact]
    public async Task Representable_dates_in_year_9999_do_not_overflow_calendar_validation()
    {
        var start = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await _service.CreateAsync(NewCover(start, start.AddDays(1)));

        Assert.Equal(3946.25m, Assert.Single(_covers.Items).Premium);
    }

    private async Task<BusinessValidationException> AssertRejectedAsync(Cover cover, string field)
    {
        var id = cover.Id;
        var start = cover.StartDate;
        var end = cover.EndDate;
        var premium = cover.Premium;
        var computeError = Assert.Throws<BusinessValidationException>(() =>
            _service.ComputePremium(start, end, cover.Type));
        var createError = await Assert.ThrowsAsync<BusinessValidationException>(() => _service.CreateAsync(cover));

        Assert.Contains(field, createError.Errors.Keys);
        Assert.Equal(computeError.Errors.Keys.Order(), createError.Errors.Keys.Order());
        foreach (var key in createError.Errors.Keys)
            Assert.Equal(computeError.Errors[key], createError.Errors[key]);
        Assert.Equal(id, cover.Id);
        Assert.Equal(start, cover.StartDate);
        Assert.Equal(start.Kind, cover.StartDate.Kind);
        Assert.Equal(end, cover.EndDate);
        Assert.Equal(end.Kind, cover.EndDate.Kind);
        Assert.Equal(premium, cover.Premium);
        Assert.Empty(_covers.Items);
        Assert.Empty(_calls);
        return createError;
    }

    private static DateTime ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).UtcDateTime;

    private static Cover NewCover(DateTime start, DateTime end) => new()
    {
        Id = "client-id", StartDate = start, EndDate = end, Type = CoverType.Yacht, Premium = -1m
    };
}
