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
        public async Task<IEnumerable<ClaimContract>> GetAsync()
        {
            return (await _claimService.GetAllAsync()).Select(claim => claim.ToDto());
        }

        [HttpPost]
        public async Task<ActionResult> CreateAsync(ClaimContract claim)
        {
            var createdClaim = await _claimService.CreateAsync(claim.ToDomain());
            return Ok(createdClaim.ToDto());
        }

        [HttpDelete("{id}")]
        public async Task DeleteAsync(string id)
        {
            await _claimService.DeleteAsync(id);
        }

        [HttpGet("{id}")]
        public async Task<ClaimContract> GetAsync(string id)
        {
            return (await _claimService.GetByIdAsync(id)).ToDto();
        }
    }
}
