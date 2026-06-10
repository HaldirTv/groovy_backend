using System.Text.Json;
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly ReglogService _reglogService;
    private readonly TokenService _tokenService;

    public AuthController(ReglogService reglogService, TokenService tokenService)  
    {
        _reglogService = reglogService;
        _tokenService = tokenService;
    }

    [HttpGet("test")]
    public IActionResult Test() => Ok(new { Message = "Auth Microservice is running successfully!" });

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.RegisterUnVerifiedAsync(dto, cancellationToken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "User registered successfully! You have 10 minutes to verify." });
    }

    [HttpPost("confirmregister")]
    public async Task<IActionResult> ConfirmRegister(ConfirmRegisterDto confirmRegisterDto, CancellationToken ctoken = default)
    {
        var result = await _reglogService.ConfirmEmailAsync(confirmRegisterDto.Email, confirmRegisterDto.Code, ctoken);
        if(!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        
        return Ok(new { Message = "User verified successfully!", Token = _tokenService.GenerateToken(result.Data) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.ValidateUserForLoginAsync(dto, cancellationToken);
        if (!result.Success) return Unauthorized(new { Message = result.ErrorMessage });

        var user = result.Data;
        string deviceId = dto.DeviceId ?? "default_web_client";
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, cancellationToken);
        
        Response.Cookies.Append("refreshToken", refreshToken, new CookieOptions 
        { 
            HttpOnly = true, 
            Secure = true, 
            SameSite = SameSiteMode.None, 
            Expires = DateTimeOffset.UtcNow.AddDays(30) 
        });

        return Ok(new { Message = "User logged in successfully!", Token = _tokenService.GenerateToken(user) });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto)
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var clientToken))
            return Unauthorized(new { Message = "Refresh token cookie is missing." });

        var deviceId = dto.DeviceId ?? "default_web_client";
        var valResult = await _reglogService.ValidateRefreshTokenAsync(dto.Email, deviceId, clientToken);
        if (!valResult.Success) return Unauthorized(new { Message = valResult.ErrorMessage });

        var user = await _reglogService.FindUserByEmailAsync(dto.Email);
        if (user == null) return NotFound(new { Message = "User not found." });

        return Ok(new { Token = _tokenService.GenerateToken(user) });
    }
    
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutDto dto, CancellationToken ctoken)
    {
        string deviceId = dto.DeviceId ?? "default_web_client";
        await _reglogService.RemoveSessionAsync(dto.Email, deviceId);
        Response.Cookies.Delete("refreshToken");
        return Ok(new { Message = "User logged out." });
    }

    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto, CancellationToken ctoken = default)
    {
        try
        {
            if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
                return Unauthorized(new { Message = "User ID invalid or missing." });
            
            var user = await _reglogService.FindUserByIdAsync(userId, true, ctoken);
            if (user == null) return NotFound(new { Message = "User not found." });

            var passwordResult = await _reglogService.ChangePasswordAsync(user, dto.OldPassword, dto.NewPassword, ctoken);
            if (!passwordResult.Success) return BadRequest(new { Message = passwordResult.ErrorMessage });

            await _reglogService.RevokeAllSessionsAsync(user.Email, ctoken);
            return Ok(new { Message = "Password changed successfully!" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAll(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized();

        var user = await _reglogService.FindUserByIdAsync(userId, false, ctoken);
        if (user == null) return NotFound();

        await _reglogService.RevokeAllSessionsAsync(user.Email, ctoken);
        Response.Cookies.Delete("refreshToken");
        return Ok(new { Message = "Все сессии аннулированы." });
    }
    
    [HttpPost("requestresetpassword")]
    public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _reglogService.FindUserByEmailAsync(dto.Email, true, cancellationToken);
        if (user == null) return NotFound(new { Message = "No user found." });
        
        var resetResult = await _reglogService.RequestPasswordResetAsync(user, cancellationToken);
        if (!resetResult.Success) return BadRequest(new { Message = resetResult.ErrorMessage });

        await _reglogService.SaveResetCodeAsync(dto.Email, resetResult.Data);
        return Ok(new { Message = "Password reset email sent!" });
    }

    [HttpPost("verifycodepasswordreset")]
    public async Task<IActionResult> VerifyCodePasswordReset([FromBody] VerifyCodeDto dto, CancellationToken cancellationToken = default)
    {
        if (!await _reglogService.VerifyResetCodeAsync(dto.Email, dto.Code))
            return BadRequest(new { Message = "Invalid or expired code." });

        string resetToken = Guid.NewGuid().ToString("N");
        await _reglogService.SaveVerifiedTokenAsync(dto.Email, resetToken);
        
        return Ok(new { Message = "Code verified!", Token = resetToken });
    }

    [HttpPost("confirmresetpassword")]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] ConfirmResetPasswordDto dto, CancellationToken cancellationToken = default)
    {
        if (!await _reglogService.VerifyResetTokenAsync(dto.Email, dto.Token))
            return BadRequest(new { Message = "Invalid or expired token." });
        
        var user = await _reglogService.FindUserByEmailAsync(dto.Email, true, cancellationToken);
        if (user == null) return NotFound();

        var passwordResult = await _reglogService.ChangePasswordAsync(user, dto.NewPassword, cancellationToken);
        if (!passwordResult.Success) return BadRequest(new { Message = passwordResult.ErrorMessage });

        await _reglogService.RevokeAllSessionsAsync(user.Email, cancellationToken);
        return Ok(new { Message = "Password reset successfully!" }); 
    }

    public record class ConfirmRegisterDto(string Email, string Code);
    public record class RequestPasswordResetDto(string Email);
    public record VerifyCodeDto(string Email, string Code);
    public record ConfirmResetPasswordDto(string Email, string Token, string NewPassword);
}