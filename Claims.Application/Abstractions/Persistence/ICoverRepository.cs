namespace Claims.Repositories;

public interface ICoverRepository
{
    Task<IEnumerable<Cover>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Cover?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(Cover cover, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
