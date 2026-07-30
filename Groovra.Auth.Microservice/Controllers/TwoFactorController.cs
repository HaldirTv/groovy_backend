using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Models;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth/2fa")]
public class TwoFactorController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly TwoFactorService _twoFactorService;
    private readonly TokenService _tokenService;
    private readonly ReglogService _reglogService;
    private readonly IDistributedCache _cache;

    public TwoFactorController(
        AuthDbContext db, 
        TwoFactorService twoFactorService, 
        TokenService tokenService, 
        ReglogService reglogService, 
        IDistributedCache cache)
    {
        _db = db;
        _twoFactorService = twoFactorService;
        _tokenService = tokenService;
        _reglogService = reglogService;
        _cache = cache;
    }

    private async Task<User?> GetUserFromRequestAsync(CancellationToken ctoken, bool tracking = true)
    {
        if (Request.Headers.TryGetValue("X-User-Id", out var headerVal) && Guid.TryParse(headerVal, out Guid headerUserId))
        {
            var user = tracking 
                ? await _db.Users.FirstOrDefaultAsync(u => u.Id == headerUserId, ctoken)
                : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == headerUserId, ctoken);
            if (user != null) return user;
        }

        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            string authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string tokenStr = authStr.Substring("Bearer ".Length).Trim();
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwt = handler.ReadJwtToken(tokenStr);

                    var subClaim = jwt.Claims.FirstOrDefault(c => 
                        c.Type == ClaimTypes.NameIdentifier || 
                        c.Type == "sub" || 
                        c.Type == "nameid");

                    if (subClaim != null && Guid.TryParse(subClaim.Value, out Guid tokenUserId))
                    {
                        var user = tracking
                            ? await _db.Users.FirstOrDefaultAsync(u => u.Id == tokenUserId, ctoken)
                            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tokenUserId, ctoken);
                        if (user != null) return user;
                    }

                    var emailClaim = jwt.Claims.FirstOrDefault(c => 
                        c.Type == ClaimTypes.Email || 
                        c.Type == "email");

                    if (emailClaim != null && !string.IsNullOrWhiteSpace(emailClaim.Value))
                    {
                        string email = emailClaim.Value.Trim();
                        var user = tracking
                            ? await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ctoken)
                            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ctoken);
                        if (user != null) return user;
                    }
                }
                catch {  }
            }
        }

        return null;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ctoken)
    {
        var user = await GetUserFromRequestAsync(ctoken, tracking: false);
        if (user == null) 
            return Unauthorized(new { Message = "Потрібна повторна авторизація." });

        return Ok(new TwoFactorStatusResponseDto
        {
            IsEnabled = user.TwoFactorEnabled
        });
    }

    [HttpPost("setup")]
    public async Task<IActionResult> Setup(CancellationToken ctoken)
    {
        var user = await GetUserFromRequestAsync(ctoken, tracking: true);
        if (user == null)
            return Unauthorized(new { Message = "Потрібна повторна авторизація." });

        string secret = _twoFactorService.GenerateSecretKey();
        user.TempTwoFactorSecret = secret;
        await _db.SaveChangesAsync(ctoken);

        string otpAuthUri = _twoFactorService.GenerateOtpAuthUri(user.Email, secret);

        return Ok(new TwoFactorSetupResponseDto
        {
            SecretKey = secret,
            OtpAuthUri = otpAuthUri
        });
    }

    [HttpPost("enable")]
    public async Task<IActionResult> Enable([FromBody] TwoFactorVerifyCodeDto dto, CancellationToken ctoken)
    {
        var user = await GetUserFromRequestAsync(ctoken, tracking: true);
        if (user == null)
            return Unauthorized(new { Message = "Потрібна повторна авторизація." });

        string? secret = user.TempTwoFactorSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return BadRequest(new { Message = "Спочатку потрібно згенерувати secret через setup." });
        }

        bool isValid = _twoFactorService.VerifyTotpCode(secret, dto.Code);
        if (!isValid)
        {
            return BadRequest(new { Message = "Невірний 6-значний код авторизатора." });
        }

        var (plainRecoveryCodes, hashedJson) = _twoFactorService.GenerateRecoveryCodes();

        user.TwoFactorSecret = secret;
        user.TempTwoFactorSecret = null;
        user.TwoFactorEnabled = true;
        user.TwoFactorRecoveryCodesJson = hashedJson;

        await _db.SaveChangesAsync(ctoken);

        return Ok(new TwoFactorEnableResponseDto
        {
            Success = true,
            RecoveryCodes = plainRecoveryCodes
        });
    }

    [HttpPost("disable")]
    public async Task<IActionResult> Disable([FromBody] TwoFactorDisableDto dto, CancellationToken ctoken)
    {
        var user = await GetUserFromRequestAsync(ctoken, tracking: true);
        if (user == null)
            return Unauthorized(new { Message = "Потрібна повторна авторизація." });

        bool isPasswordValid = !string.IsNullOrWhiteSpace(dto.Password) 
            && BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash);

        bool isCodeValid = false;
        if (!string.IsNullOrWhiteSpace(user.TwoFactorSecret) && !string.IsNullOrWhiteSpace(dto.Code))
        {
            isCodeValid = _twoFactorService.VerifyTotpCode(user.TwoFactorSecret, dto.Code)
                || _twoFactorService.VerifyAndConsumeRecoveryCode(user, dto.Code);
        }

        if (!isPasswordValid && !isCodeValid)
        {
            return BadRequest(new { Message = "Введіть правильний пароль облікового запису АБО 6-значний код 2FA / код відновлення." });
        }

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TempTwoFactorSecret = null;
        user.TwoFactorRecoveryCodesJson = null;

        await _db.SaveChangesAsync(ctoken);

        return Ok(new { Message = "Двофакторну автентифікацію успішно вимкнено." });
    }

    [HttpPost("verify-login")]
    public async Task<IActionResult> VerifyLogin([FromBody] TwoFactorLoginVerifyDto dto, CancellationToken ctoken)
    {
        if (string.IsNullOrWhiteSpace(dto.Ticket) || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest(new { Message = "Ticket and Code are required." });
        }

        string cacheKey = $"2fa_ticket:{dto.Ticket}";
        string? storedUserId = await _cache.GetStringAsync(cacheKey, ctoken);
        if (string.IsNullOrEmpty(storedUserId) || !Guid.TryParse(storedUserId, out Guid userId))
        {
            return Unauthorized(new { Message = "Сесія 2FA застаріла або недійсна. Спробуйте увійти знову." });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        if (user == null || !user.TwoFactorEnabled || string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            return Unauthorized(new { Message = "Користувача не знайдено або 2FA вимкнено." });
        }

        bool isValidCode = _twoFactorService.VerifyTotpCode(user.TwoFactorSecret, dto.Code);
        if (!isValidCode)
        {
            isValidCode = _twoFactorService.VerifyAndConsumeRecoveryCode(user, dto.Code);
            if (isValidCode)
            {
                await _db.SaveChangesAsync(ctoken);
            }
        }

        if (!isValidCode)
        {
            return BadRequest(new { Message = "Невірний 6-значний код або резервний код." });
        }

        await _cache.RemoveAsync(cacheKey, ctoken);

        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, ctoken);

        bool secure = IsSecureRequest();
        Response.Cookies.Append("refreshToken", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/"
        });

        return Ok(new
        {
            Message = "User logged in successfully!",
            Token = _tokenService.GenerateToken(user, deviceId)
        });
    }

    // Дублирует AuthController.IsSecureRequest() - за гейтвеем Request.IsHttps всегда false
    // (внутренний трафик plain HTTP), реальную схему нужно смотреть через X-Forwarded-Proto,
    // иначе refresh-cookie после 2FA-логина уходит с Secure=false/SameSite=Lax и браузер
    // может её отбросить в проде, хотя обычный логин через AuthController уже это учитывает.
    private bool IsSecureRequest()
    {
        if (Request.IsHttps) return true;
        if (string.Equals(Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase)) return true;
        var host = Request.Host.Host;
        if (!string.IsNullOrEmpty(host) && host != "localhost" && host != "127.0.0.1") return true;
        return false;
    }
}
