using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;

namespace MyNearestCentersApi.Controllers
{
    [ApiController]
    [Route("api/centers")]
    public class WaitingTimeController : ControllerBase
    {
        public class WaitingTimeRequest
        {
            public int SiteId { get; set; }
        }

        [HttpPost("wait")]
        public IActionResult GetWaitingTime([FromBody] WaitingTimeRequest request)
        {
            // Generate random totalOP between 1 and 10 patients
            var random = new Random();
            int totalOP = random.Next(1, 11);

            var response = new
            {
                dataValues = new[]
                {
                    new
                    {
                        totalOP,
                        UpdatedTime = "12:35 PM"
                    }
                }
            };

            return Ok(response);
        }
    }
}