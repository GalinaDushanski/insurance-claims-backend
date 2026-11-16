using Claims.Services;
using Claims.Api.Contracts;
using ClaimContract = Claims.Api.Contracts.Claim;
using Microsoft.AspNetCore.Mvc;

namespace Claims.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ClaimsController : ControllerBase
    {
        private readonly IClaimService _claimService;

        public ClaimsController(IClaimService claimService)
        {
            _claimService = claimService;
        }

        [HttpGet]
        public async Task<IEnumerable<ClaimContract>> GetAsync(CancellationToken cancellationToken)
        {
            return (await _claimService.GetAllAsync(cancellationToken)).Select(claim => claim.ToDto());
        }

        [HttpPost]
        public async Task<ActionResult> CreateAsync(ClaimContract claim, CancellationToken cancellationToken)
        {
            var createdClaim = await _claimService.CreateAsync(claim.ToDomain(), cancellationToken);
            return CreatedAtAction("Get", new { id = createdClaim.Id }, createdClaim.ToDto());
        }

        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
        {
            await _claimService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }

        [HttpGet("{id}")]
        public async Task<ClaimContract> GetAsync(string id, CancellationToken cancellationToken)
        {
            return (await _claimService.GetByIdAsync(id, cancellationToken)).ToDto();
        }
    }
}
