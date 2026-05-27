using Microsoft.AspNetCore.Mvc;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("[controller]")]
public class AuthController : ControllerBase
{
    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok(new { Message = "Auth Microservice is running successfully!" });
    }
}