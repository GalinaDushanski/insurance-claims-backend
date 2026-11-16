namespace Claims.Services;

public interface ICoverService
{
    Task<IEnumerable<Cover>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Cover> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<Cover> CreateAsync(Cover cover, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType, CancellationToken cancellationToken = default);
}
