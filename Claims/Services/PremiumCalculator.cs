namespace Claims.Services;

public class PremiumCalculator
{
    public decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType)
    {
        if (endDate <= startDate)
            throw new ArgumentOutOfRangeException(nameof(endDate), "EndDate must be after StartDate.");

        var durationTicks = (endDate - startDate).Ticks;
        var billableDays = durationTicks / TimeSpan.TicksPerDay;
        if (durationTicks % TimeSpan.TicksPerDay != 0) billableDays++;

        const decimal baseDailyRate = 1250m;
        var multiplier = coverType switch
        {
            CoverType.Yacht => 1.10m,
            CoverType.PassengerShip => 1.20m,
            CoverType.Tanker => 1.50m,
            _ => 1.30m
        };
        var dailyRate = baseDailyRate * multiplier;

        var fullRateDays = Math.Min(billableDays, 30);
        var middleRateDays = Math.Min(Math.Max(billableDays - 30, 0), 150);
        var finalRateDays = Math.Max(billableDays - 180, 0);
        var middleDiscount = coverType == CoverType.Yacht ? 0.05m : 0.02m;
        var finalDiscount = coverType == CoverType.Yacht ? 0.08m : 0.03m;

        return dailyRate * fullRateDays
            + dailyRate * middleRateDays * (1m - middleDiscount)
            + dailyRate * finalRateDays * (1m - finalDiscount);
    }
}
