using System.Globalization;
using Claims.Services;
using Xunit;

namespace Claims.Tests.Unit;

public class PremiumCalculatorTests
{
    private readonly PremiumCalculator _calculator = new();
    private static readonly DateTime Start = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(CoverType.Yacht, "1375")]
    [InlineData(CoverType.PassengerShip, "1500")]
    [InlineData(CoverType.ContainerShip, "1625")]
    [InlineData(CoverType.BulkCarrier, "1625")]
    [InlineData(CoverType.Tanker, "1875")]
    public void One_day_uses_the_type_adjusted_base_rate(CoverType type, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(1), type));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 29, "39875")]
    [InlineData(CoverType.Yacht, 30, "41250")]
    [InlineData(CoverType.Yacht, 31, "42556.25")]
    [InlineData(CoverType.Yacht, 179, "235881.25")]
    [InlineData(CoverType.Yacht, 180, "237187.50")]
    [InlineData(CoverType.Yacht, 181, "238452.50")]
    [InlineData(CoverType.ContainerShip, 29, "47125")]
    [InlineData(CoverType.ContainerShip, 30, "48750")]
    [InlineData(CoverType.ContainerShip, 31, "50342.50")]
    [InlineData(CoverType.ContainerShip, 179, "286032.50")]
    [InlineData(CoverType.ContainerShip, 180, "287625")]
    [InlineData(CoverType.ContainerShip, 181, "289201.25")]
    public void Tier_boundaries_charge_each_day_in_exactly_one_tier(CoverType type, int days, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(days), type));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 200, "262487.50")]
    [InlineData(CoverType.ContainerShip, 200, "319150")]
    [InlineData(CoverType.PassengerShip, 30, "45000")]
    [InlineData(CoverType.PassengerShip, 31, "46470")]
    [InlineData(CoverType.PassengerShip, 180, "265500")]
    [InlineData(CoverType.PassengerShip, 181, "266955")]
    [InlineData(CoverType.PassengerShip, 200, "294600")]
    [InlineData(CoverType.PassengerShip, 365, "534675")]
    [InlineData(CoverType.Tanker, 30, "56250")]
    [InlineData(CoverType.Tanker, 31, "58087.50")]
    [InlineData(CoverType.Tanker, 180, "331875")]
    [InlineData(CoverType.Tanker, 181, "333693.75")]
    [InlineData(CoverType.Tanker, 200, "368250")]
    [InlineData(CoverType.Tanker, 365, "668343.75")]
    public void Progressive_totals_match_independently_calculated_examples(CoverType type, int days, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(days), type));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 365, "471212.50")]
    [InlineData(CoverType.Yacht, 366, "472477.50")]
    [InlineData(CoverType.ContainerShip, 365, "579231.25")]
    [InlineData(CoverType.ContainerShip, 366, "580807.50")]
    public void Year_long_periods_charge_all_days_including_day_366(CoverType type, int days, string expected)
    {
        var start = new DateTime(2031, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(start, start.AddDays(days), type));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Equal_or_reversed_dates_reject_end_date(long ticksAfterStart)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _calculator.ComputePremium(Start, Start.AddTicks(ticksAfterStart), CoverType.Yacht));

        Assert.Equal("endDate", error.ParamName);
    }

    [Theory]
    [InlineData(1L, "1375")]
    [InlineData(TimeSpan.TicksPerHour, "1375")]
    [InlineData(TimeSpan.TicksPerDay, "1375")]
    [InlineData(TimeSpan.TicksPerDay + 1, "2750")]
    [InlineData(25 * TimeSpan.TicksPerHour, "2750")]
    public void Positive_partial_days_round_up_using_elapsed_24_hour_periods(long ticks, string expected)
    {
        var start = Start.AddHours(23);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(start, start.AddTicks(ticks), CoverType.Yacht));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 30, "42556.25")]
    [InlineData(CoverType.Yacht, 180, "238452.50")]
    [InlineData(CoverType.ContainerShip, 30, "50342.50")]
    [InlineData(CoverType.ContainerShip, 180, "289201.25")]
    public void Partial_day_after_a_tier_boundary_uses_the_next_tiers_rate(CoverType type, int fullDays, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(fullDays).AddTicks(1), type));
    }

    [Fact]
    public void Unknown_cover_type_preserves_the_other_type_multiplier()
    {
        Assert.Equal(1625m, _calculator.ComputePremium(Start, Start.AddDays(1), (CoverType)99));
    }
}
