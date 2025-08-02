using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreApp
{
    [Route("[controller]")]
    [ApiController]
    public class DataController : ControllerBase
    {
        [HttpPost("greet")]
        public IActionResult GreetUser([FromBody] DataRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Req))
            {
                return BadRequest("İsim boş olamaz.");
            }

            string message = $"Merhaba, {request.Req}!";
            return Ok(new { Message = message });
        }
    }
}
