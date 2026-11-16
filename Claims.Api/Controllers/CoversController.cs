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
    [ProducesResponseType(typeof(decimal), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ComputePremiumAsync(
        [FromQuery, ModelBinder(BinderType = typeof(UtcDateTimeModelBinder))] DateTime startDate,
        [FromQuery, ModelBinder(BinderType = typeof(UtcDateTimeModelBinder))] DateTime endDate,
        [FromQuery] CoverType coverType,
        CancellationToken cancellationToken)
    {
        return Ok(_coverService.ComputePremium(startDate, endDate, coverType, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CoverContract>>> GetAsync(CancellationToken cancellationToken)
    {
        var results = await _coverService.GetAllAsync(cancellationToken);
        return Ok(results.Select(cover => cover.ToDto()));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CoverContract), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CoverContract>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var cover = await _coverService.GetByIdAsync(id, cancellationToken);
        return Ok(cover.ToDto());
    }

    [HttpPost]
    [ProducesResponseType(typeof(CoverContract), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CreateAsync(CoverContract cover, CancellationToken cancellationToken)
    {
        var createdCover = await _coverService.CreateAsync(cover.ToDomain(), cancellationToken);
        return CreatedAtAction("Get", new { id = createdCover.Id }, createdCover.ToDto());
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _coverService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
