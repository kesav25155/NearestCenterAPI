using Microsoft.AspNetCore.Mvc;
using MyNearestCentersApi.Services;
using System.Threading.Tasks;

namespace MyNearestCentersApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CentersController : ControllerBase
    {
        private readonly NearestCentersService _service;

        public CentersController(NearestCentersService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        [HttpPost("nearest")]
        public async Task<IActionResult> GetNearestCenters([FromBody] AddressRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Address))
                return BadRequest("Address is required");

            var result = await _service.GetNearestCentersAsync(request.Address);
            return Content(result, "application/json");
        }
    }

    public class AddressRequest
    {
        public string? Address { get; set; }
    }
}