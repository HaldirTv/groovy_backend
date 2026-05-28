using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    ReglogService _reglogService;

    public AuthController(ReglogService reglogService)
    {
        _reglogService = reglogService;
    }

    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok(new { Message = "Auth Microservice is running successfully!" });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var user = await _reglogService.RegisterAsync(dto);
        if (user == null)
        {
            return BadRequest(new { Message = "Registration failed. Email might already be in use." });
        }
        return Ok(new { Message = "User registered successfully!" });
    }
}