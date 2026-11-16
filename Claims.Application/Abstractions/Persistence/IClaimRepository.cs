namespace Claims.Repositories;

public interface IClaimRepository
{
    Task<IEnumerable<Claim>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Claim?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(Claim claim, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
