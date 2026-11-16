using Claims.Services;
using Claims.Api.Contracts;
using CoverContract = Claims.Api.Contracts.Cover;
using Claims.ModelBinding;
using Microsoft.AspNetCore.Mvc;

namespace Claims.Controllers;

[ApiController]
[Route("[controller]")]
public class CoversController : ControllerBase
{
    private readonly ICoverService _coverService;

    public CoversController(ICoverService coverService)
    {
        _coverService = coverService;
    }

    [HttpGet("compute")]
    public async Task<ActionResult> ComputePremiumAsync(
        [FromQuery, ModelBinder(BinderType = typeof(UtcDateTimeModelBinder))] DateTime startDate,
        [FromQuery, ModelBinder(BinderType = typeof(UtcDateTimeModelBinder))] DateTime endDate,
        [FromQuery] CoverType coverType)
    {
        return Ok(_coverService.ComputePremium(startDate, endDate, coverType));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CoverContract>>> GetAsync()
    {
        var results = await _coverService.GetAllAsync();
        return Ok(results.Select(cover => cover.ToDto()));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CoverContract>> GetAsync(string id)
    {
        var cover = await _coverService.GetByIdAsync(id);
        return Ok(cover.ToDto());
    }

    [HttpPost]
    public async Task<ActionResult> CreateAsync(CoverContract cover)
    {
        var createdCover = await _coverService.CreateAsync(cover.ToDomain());
        return CreatedAtAction("Get", new { id = createdCover.Id }, createdCover.ToDto());
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteAsync(string id)
    {
        await _coverService.DeleteAsync(id);
        return NoContent();
    }
}
