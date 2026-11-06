namespace Claims.Repositories;

public interface ICoverRepository
{
    Task<IEnumerable<Cover>> GetAllAsync();
    Task<Cover?> GetByIdAsync(string id);
    Task AddAsync(Cover cover);
    Task DeleteAsync(string id);
}
