namespace Claims.Services;

public interface IClaimService
{
    Task<IEnumerable<Claim>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Claim> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<Claim> CreateAsync(Claim claim, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
