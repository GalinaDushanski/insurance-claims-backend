using Claims.Auditing;
using Claims.Exceptions;
using Claims.Repositories;
using Claims.Validation;

namespace Claims.Services;

public class ClaimService : IClaimService
{
    private readonly IClaimRepository _repository;
    private readonly IAuditer _auditer;
    private readonly ICoverRepository _covers;

    public ClaimService(IClaimRepository repository, IAuditer auditer, ICoverRepository covers)
    {
        _repository = repository;
        _auditer = auditer;
        _covers = covers;
    }

    public Task<IEnumerable<Claim>> GetAllAsync()
    {
        return _repository.GetAllAsync();
    }

    public async Task<Claim> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id)
            ?? throw new ResourceNotFoundException("Claim", id);
    }

    public async Task<Claim> CreateAsync(Claim claim)
    {
        var createdUtc = await ValidateAsync(claim);
        claim.Created = createdUtc;
        claim.Id = Guid.NewGuid().ToString();
        await _repository.AddAsync(claim);
        await _auditer.AuditClaimAsync(claim.Id, "POST");
        return claim;
    }

    public async Task DeleteAsync(string id)
    {
        await GetByIdAsync(id);
        await _repository.DeleteAsync(id);
        await _auditer.AuditClaimAsync(id, "DELETE");
    }

    private async Task<DateTime> ValidateAsync(Claim claim)
    {
        var errors = new Dictionary<string, string[]>();
        if (claim.DamageCost < 0m || claim.DamageCost > 100000m)
            errors["damageCost"] = ["DamageCost must be between 0 and 100000, inclusive."];

        var createdUtc = AsUtc(claim.Created);
        if (createdUtc is null)
            errors["created"] = ["Created must be UTC or a date without a timezone; local DateTime values are not supported."];

        if (string.IsNullOrWhiteSpace(claim.CoverId))
        {
            errors["coverId"] = ["CoverId is required."];
        }
        else
        {
            var cover = await _covers.GetByIdAsync(claim.CoverId);
            if (cover is null)
            {
                errors["coverId"] = ["The referenced cover does not exist."];
            }
            else
            {
                var startUtc = AsUtc(cover.StartDate);
                var endUtc = AsUtc(cover.EndDate);
                if (startUtc is null || endUtc is null)
                    errors["coverId"] = ["The referenced cover must have UTC dates or dates without a timezone."];
                else if (createdUtc is not null && (createdUtc < startUtc || createdUtc > endUtc))
                    errors["created"] = ["Created must fall within the cover period, including its start and end."];
            }
        }

        if (errors.Count > 0) throw new BusinessValidationException(errors);
        return createdUtc!.Value;
    }

    private static DateTime? AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => null
    };
}
