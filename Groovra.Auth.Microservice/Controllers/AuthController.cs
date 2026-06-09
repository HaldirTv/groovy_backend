using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    ReglogService _reglogService;
    TokenService _tokenService;
    IDistributedCache _cache;
    public AuthController(ReglogService reglogService, IDistributedCache distributedCache,TokenService tokenService)  
    {
        _reglogService = reglogService;
        _cache = distributedCache;
        _tokenService = tokenService;
    }


    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto,CancellationToken cancellationToken = default)
    {
        var user = await _reglogService.RegisterAsync(dto,cancellationToken);
        if (user == null)
        {
            return BadRequest(new { Message = "Registration failed. Email might already be in use." });
        }
        return Ok(new { Message = "User registered successfully!" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto,CancellationToken cancellationToken = default)
    {
       var user = await _reglogService.ValidateUserForLoginAsync(dto,cancellationToken);
       if(user ==null){
           return Unauthorized(new { Message = "Invalid email or password." });
       }
       string accessToken = _tokenService.GenerateToken(user);
       
       string refreshToken = Guid.NewGuid().ToString("N");
       string deviceId = dto.DeviceId ?? "default_web_client";
       string cacheKey = $"refresh:{user.Email}:{deviceId}";
       
       await _cache.SetStringAsync(cacheKey, refreshToken, new DistributedCacheEntryOptions
       {
           AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
       }, cancellationToken);
       
       Response.Cookies.Append("refreshToken", refreshToken, new CookieOptions
       {
           HttpOnly = true,       // Защита от XSS (JS-скрипты фронта не смогут украсть токен)
           Secure = true,
           SameSite = SameSiteMode.None,//!!!надо будет подумать 
           Expires = DateTimeOffset.UtcNow.AddDays(30)
       });
       
       return Ok(new { Message = "User logged in successfully!", Token = accessToken });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var clientRefreshToken))
        {
            return Unauthorized(new { Message = "Refresh token cookie is missing." });
        }
        string deviceId = dto.DeviceId ?? "default_web_client";
        string cacheKey = $"refresh:{dto.Email}:{deviceId}";
        string? savedRefreshToken = await _cache.GetStringAsync(cacheKey, cancellationToken);
        
        if (string.IsNullOrEmpty(savedRefreshToken) || savedRefreshToken != clientRefreshToken)
        {
            return Unauthorized(new { Message = "Invalid or expired refresh token. Please login again." });
        }
        
        var user = await _reglogService.FindUserByEmailAsync(dto.Email,true,cancellationToken);
        if (user == null) return NotFound(new { Message = "User not found." });
        
        string newAccessToken = _tokenService.GenerateToken(user);

        return Ok(new { Token = newAccessToken });
        
    }
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody]LogoutDto dto)
    {
        string deviceId = dto.DeviceId ?? "default_web_client";
        string cacheKey = $"refresh:{dto.Email}:{deviceId}";
        await _cache.RemoveAsync(cacheKey);
        Response.Cookies.Delete("refreshToken");
        return Ok(new { Message = "User logged out." });
    }

    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto, CancellationToken ctoken = default)
    {
        try
        {
            var userIdHeader = Request.Headers["X-User-Id"].ToString();
            if (string.IsNullOrWhiteSpace(userIdHeader))
            {
                return Unauthorized(new { Message = "User ID header is missing." });
            }
            if (!Guid.TryParse(userIdHeader, out Guid userId))
            {
                return BadRequest(new { message = "Невірний формат GUID в заголовке X-User-Id." });
            }
            
            var user = await _reglogService.FindUserByIdAsync(userId,true,ctoken);
            if (user == null)
            {
                return NotFound(new { Message = "User not found." });
            }
            await _reglogService.ChangePasswordAsync(user, dto.OldPassword, dto.NewPassword, ctoken);
            return Ok(new { Message = "Password changed successfully!" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = $"Exception: {ex.Message}" });
        }
    }

    
    
    //Endpoints для збросу пароля по пошті
    [HttpPost("requestresetpassword")]
    public async Task<IActionResult> RequestPasswordReset([FromBody]RequestPasswordResetDto dto,CancellationToken cancellationToken = default)
    {
        var user = await _reglogService.FindUserByEmailAsync(dto.Email,true,cancellationToken);
        if(user==null)return NotFound(new { Message = "No user found with the provided email." });
        string code = await _reglogService.RequestPasswordResetAsync(user, cancellationToken);
        
        var cacheOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        };
        await _cache.SetStringAsync($"password_reset:{dto.Email}",code, cacheOptions, cancellationToken);
        
        
        return Ok(new { Message = "Password reset email sent successfully!" });
    }
    [HttpPost("verifycode")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeDto dto, CancellationToken cancellationToken = default)
    {
        var cachedCode = await _cache.GetStringAsync($"password_reset:{dto.Email}", cancellationToken);;
        if (cachedCode == null || cachedCode != dto.Code)
        {
            return BadRequest(new { Message = "Invalid or expired code." });
        }
        await _cache.RemoveAsync($"password_reset:{dto.Email}", cancellationToken);;
        string resetToken = Guid.NewGuid().ToString("N");
        
        await _cache.SetStringAsync($"verified_token:{dto.Email}", resetToken, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        }, cancellationToken);
        
        return Ok(new { Message = "Code verified successfully!", Token = resetToken });
    }

    [HttpPost("confirmresetpassword")]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] ConfirmResetPasswordDto dto,CancellationToken cancellationToken = default)
    {
        string cachedToken = await _cache.GetStringAsync($"verified_token:{dto.Email}", cancellationToken);
        if (cachedToken == null || cachedToken != dto.Token){
            return BadRequest(new { Message = "Invalid or expired token." });
        }
        await  _cache.RemoveAsync($"verified_token:{dto.Email}", cancellationToken);
        
        var user = await _reglogService.FindUserByEmailAsync(dto.Email,true,cancellationToken);;
        if (user == null)
        {
            return NotFound(new { Message = "User not found." });
        }
        await _reglogService.ChangePasswordAsync(user, dto.NewPassword, cancellationToken);
        return Ok(new { Message = "Password reset successfully!" }); 
    }
    
    
    
    
    
    public record class RequestPasswordResetDto(string Email);
    public record VerifyCodeDto(string Email, string Code);
    public record ConfirmResetPasswordDto(string Email, string Token, string NewPassword);
}