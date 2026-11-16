using Claims.Auditing;
using Claims.Domain.Services;
using Claims.Exceptions;
using Claims.Repositories;
using Claims.Validation;

namespace Claims.Services;

public class CoverService : ICoverService
{
    private static readonly TimeSpan AuditEnqueueTimeout = TimeSpan.FromSeconds(5);
    private readonly ICoverRepository _repository;
    private readonly IAuditer _auditer;
    private readonly IPremiumCalculator _premiumCalculator;
    private readonly TimeProvider _timeProvider;

    public CoverService(ICoverRepository repository, IAuditer auditer, IPremiumCalculator premiumCalculator, TimeProvider timeProvider)
    {
        _repository = repository;
        _auditer = auditer;
        _premiumCalculator = premiumCalculator;
        _timeProvider = timeProvider;
    }

    public Task<IEnumerable<Cover>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetAllAsync(cancellationToken);
    }

    public async Task<Cover> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new ResourceNotFoundException("Cover", id);
    }

    public async Task<Cover> CreateAsync(Cover cover, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (startUtc, endUtc) = ValidatePeriod(cover.StartDate, cover.EndDate, cover.Type, "type");
        var premium = _premiumCalculator.ComputePremium(startUtc, endUtc, cover.Type);
        cover.StartDate = startUtc;
        cover.EndDate = endUtc;
        cover.Id = Guid.NewGuid().ToString();
        cover.Premium = premium;
        await _repository.AddAsync(cover, cancellationToken);
        // Persistence succeeded: client cancellation must not interrupt audit acceptance.
        using var auditTimeout = new CancellationTokenSource(AuditEnqueueTimeout);
        await _auditer.AuditCoverAsync(cover.Id, AuditAction.Created, auditTimeout.Token);
        return cover;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await GetByIdAsync(id, cancellationToken);
        await _repository.DeleteAsync(id, cancellationToken);
        // Persistence succeeded: client cancellation must not interrupt audit acceptance.
        using var auditTimeout = new CancellationTokenSource(AuditEnqueueTimeout);
        await _auditer.AuditCoverAsync(id, AuditAction.Deleted, auditTimeout.Token);
    }

    public decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (startUtc, endUtc) = ValidatePeriod(startDate, endDate, coverType, "coverType");
        return _premiumCalculator.ComputePremium(startUtc, endUtc, coverType);
    }

    private (DateTime StartUtc, DateTime EndUtc) ValidatePeriod(
        DateTime startDate, DateTime endDate, CoverType coverType, string typeField)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(coverType))
            errors[typeField] = ["CoverType must be a defined value."];

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
