using Claims.Data;
using Microsoft.EntityFrameworkCore;

namespace Claims.Repositories;

public class CoverRepository : ICoverRepository
{
    private readonly ClaimsContext _context;

    public CoverRepository(ClaimsContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<Cover>> GetAllAsync()
    {
        return await _context.Covers.ToListAsync();
    }

    public async Task<Cover?> GetByIdAsync(string id)
    {
        return await _context.Covers
            .Where(cover => cover.Id == id)
            .SingleOrDefaultAsync();
    }

    public async Task AddAsync(Cover cover)
    {
        _context.Covers.Add(cover);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var cover = await GetByIdAsync(id);
        if (cover is not null)
        {
            _context.Covers.Remove(cover);
            await _context.SaveChangesAsync();
        }
    }
}
