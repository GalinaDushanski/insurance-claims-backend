using Claims.Auditing;
using Claims.Exceptions;
using Claims.Repositories;
using Claims.Validation;

namespace Claims.Services;

public class CoverService : ICoverService
{
    private readonly ICoverRepository _repository;
    private readonly IAuditer _auditer;
    private readonly PremiumCalculator _premiumCalculator;
    private readonly TimeProvider _timeProvider;

    public CoverService(ICoverRepository repository, IAuditer auditer, PremiumCalculator premiumCalculator, TimeProvider timeProvider)
    {
        _repository = repository;
        _auditer = auditer;
        _premiumCalculator = premiumCalculator;
        _timeProvider = timeProvider;
    }

    public Task<IEnumerable<Cover>> GetAllAsync()
    {
        return _repository.GetAllAsync();
    }

    public async Task<Cover> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id)
            ?? throw new ResourceNotFoundException("Cover", id);
    }

    public async Task<Cover> CreateAsync(Cover cover)
    {
        var (startUtc, endUtc) = ValidatePeriod(cover.StartDate, cover.EndDate);
        var premium = _premiumCalculator.ComputePremium(startUtc, endUtc, cover.Type);
        cover.StartDate = startUtc;
        cover.EndDate = endUtc;
        cover.Id = Guid.NewGuid().ToString();
        cover.Premium = premium;
        await _repository.AddAsync(cover);
        await _auditer.AuditCoverAsync(cover.Id, "POST");
        return cover;
    }

    public async Task DeleteAsync(string id)
    {
        await GetByIdAsync(id);
        await _repository.DeleteAsync(id);
        await _auditer.AuditCoverAsync(id, "DELETE");
    }

    public decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType)
    {
        var (startUtc, endUtc) = ValidatePeriod(startDate, endDate);
        return _premiumCalculator.ComputePremium(startUtc, endUtc, coverType);
    }

    private (DateTime StartUtc, DateTime EndUtc) ValidatePeriod(DateTime startDate, DateTime endDate)
    {
        var errors = new Dictionary<string, string[]>();
        var startUtc = AsUtc(startDate);
        var endUtc = AsUtc(endDate);
        var todayUtc = _timeProvider.GetUtcNow().UtcDateTime.Date;

        if (startUtc is null)
            errors["startDate"] = ["StartDate must be UTC or a date without a timezone; local DateTime values are not supported."];
        else if (startUtc.Value.Date < todayUtc)
            errors["startDate"] = ["StartDate must not be before today's UTC date."];

        if (endUtc is null)
            errors["endDate"] = ["EndDate must be UTC or a date without a timezone; local DateTime values are not supported."];
        else if (startUtc is not null)
        {
            if (endUtc <= startUtc)
                errors["endDate"] = ["EndDate must be after StartDate."];
            else if (startUtc.Value.Year < DateTime.MaxValue.Year && endUtc > startUtc.Value.AddYears(1))
                errors["endDate"] = ["The cover period must not exceed one calendar year."];
        }

        if (errors.Count > 0) throw new BusinessValidationException(errors);
        return (startUtc!.Value, endUtc!.Value);
    }

    private static DateTime? AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => null
    };
}
