namespace Claims.Domain.Services;

public interface IPremiumCalculator
{
    decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType);
}
