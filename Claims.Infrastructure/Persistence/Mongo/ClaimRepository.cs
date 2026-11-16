using Claims.Data;
using Microsoft.EntityFrameworkCore;

namespace Claims.Repositories;

public class ClaimRepository : IClaimRepository
{
    private readonly ClaimsContext _context;

    public ClaimRepository(ClaimsContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<Claim>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Claims.ToListAsync(cancellationToken);
    }

    public async Task<Claim?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _context.Claims
            .Where(claim => claim.Id == id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task AddAsync(Claim claim, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.Claims.Add(claim);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var claim = await GetByIdAsync(id, cancellationToken);
        if (claim is not null)
        {
            _context.Claims.Remove(claim);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
