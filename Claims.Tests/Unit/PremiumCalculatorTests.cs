using System.Globalization;
using Claims.Services;
using Xunit;

namespace Claims.Tests.Unit;

// Characterization tests preserve the legacy algorithm, not the Task 5 requirements.
public class PremiumCalculatorTests
{
    private readonly PremiumCalculator _calculator = new();
    private static readonly DateTime Start = new(2030, 1, 1);

    [Theory]
    [InlineData(CoverType.Yacht, "3946.25")]
    [InlineData(CoverType.PassengerShip, "4425")]
    [InlineData(CoverType.ContainerShip, "4793.75")]
    [InlineData(CoverType.BulkCarrier, "4793.75")]
    [InlineData(CoverType.Tanker, "5531.25")]
    public void First_day_currently_receives_three_overlapping_charges(CoverType type, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(1), type));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 29, "114441.25")]
    [InlineData(CoverType.Yacht, 30, "118387.50")]
    [InlineData(CoverType.Yacht, 31, "120958.75")]
    [InlineData(CoverType.Yacht, 179, "501503.75")]
    [InlineData(CoverType.Yacht, 180, "504075")]
    [InlineData(CoverType.Yacht, 181, "505340")]
    [InlineData(CoverType.ContainerShip, 29, "139018.75")]
    [InlineData(CoverType.ContainerShip, 30, "143812.50")]
    [InlineData(CoverType.ContainerShip, 31, "146981.25")]
    [InlineData(CoverType.ContainerShip, 179, "615956.25")]
    [InlineData(CoverType.ContainerShip, 180, "619125")]
    [InlineData(CoverType.ContainerShip, 181, "620701.25")]
    public void Current_totals_at_discount_boundaries(CoverType type, int days, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(days), type));
    }

    [Theory]
    [InlineData(CoverType.Yacht, 365, "738100")]
    [InlineData(CoverType.Yacht, 366, "738100")]
    [InlineData(CoverType.ContainerShip, 365, "910731.25")]
    [InlineData(CoverType.ContainerShip, 366, "910731.25")]
    public void Days_after_365_currently_add_no_premium(CoverType type, int days, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddDays(days), type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_reversed_period_currently_returns_zero(int days)
    {
        Assert.Equal(0m, _calculator.ComputePremium(Start, Start.AddDays(days), CoverType.Yacht));
    }

    [Theory]
    [InlineData(1, "3946.25")]
    [InlineData(25, "7892.50")]
    public void Partial_days_currently_round_up_to_whole_day_charges(int hours, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            _calculator.ComputePremium(Start, Start.AddHours(hours), CoverType.Yacht));
    }
}
