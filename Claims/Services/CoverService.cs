using Claims.Auditing;
using Claims.Repositories;

namespace Claims.Services;

public class CoverService : ICoverService
{
    private readonly ICoverRepository _repository;
    private readonly IAuditer _auditer;
    private readonly PremiumCalculator _premiumCalculator;

    public CoverService(ICoverRepository repository, IAuditer auditer, PremiumCalculator premiumCalculator)
    {
        _repository = repository;
        _auditer = auditer;
        _premiumCalculator = premiumCalculator;
    }

    public Task<IEnumerable<Cover>> GetAllAsync()
    {
        return _repository.GetAllAsync();
    }

    public Task<Cover?> GetByIdAsync(string id)
    {
        return _repository.GetByIdAsync(id);
    }

    public async Task<Cover> CreateAsync(Cover cover)
    {
        cover.Id = Guid.NewGuid().ToString();
        cover.Premium = ComputePremium(cover.StartDate, cover.EndDate, cover.Type);
        await _repository.AddAsync(cover);
        _auditer.AuditCover(cover.Id, "POST");
        return cover;
    }

    public async Task DeleteAsync(string id)
    {
        _auditer.AuditCover(id, "DELETE");
        await _repository.DeleteAsync(id);
    }

    public decimal ComputePremium(DateTime startDate, DateTime endDate, CoverType coverType)
    {
        return _premiumCalculator.ComputePremium(startDate, endDate, coverType);
    }
}
