using Claims.Auditing;
using Claims.Repositories;

namespace Claims.Services;

public class ClaimService : IClaimService
{
    private readonly IClaimRepository _repository;
    private readonly IAuditer _auditer;

    public ClaimService(IClaimRepository repository, IAuditer auditer)
    {
        _repository = repository;
        _auditer = auditer;
    }

    public Task<IEnumerable<Claim>> GetAllAsync()
    {
        return _repository.GetAllAsync();
    }

    public Task<Claim?> GetByIdAsync(string id)
    {
        return _repository.GetByIdAsync(id);
    }

    public async Task<Claim> CreateAsync(Claim claim)
    {
        claim.Id = Guid.NewGuid().ToString();
        await _repository.AddAsync(claim);
        _auditer.AuditClaim(claim.Id, "POST");
        return claim;
    }

    public async Task DeleteAsync(string id)
    {
        _auditer.AuditClaim(id, "DELETE");
        await _repository.DeleteAsync(id);
    }
}
