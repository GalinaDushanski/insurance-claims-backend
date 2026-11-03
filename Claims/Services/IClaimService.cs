namespace Claims.Services;

public interface IClaimService
{
    Task<IEnumerable<Claim>> GetAllAsync();
    Task<Claim> GetByIdAsync(string id);
    Task<Claim> CreateAsync(Claim claim);
    Task DeleteAsync(string id);
}
