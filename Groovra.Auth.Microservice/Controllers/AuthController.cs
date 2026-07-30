using System.Text.Json;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Services;
using Groovra.Shared.ServiceResult;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly ReglogService _reglogService;
    private readonly TokenService _tokenService;
    private readonly IConfiguration _configuration;
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache _cache;

    public AuthController(ReglogService reglogService, TokenService tokenService, IConfiguration configuration, Microsoft.Extensions.Caching.Distributed.IDistributedCache cache)   
    {
        _reglogService = reglogService;
        _tokenService = tokenService;
        _configuration = configuration;
        _cache = cache;
    }

    [HttpGet("test")]
    public IActionResult Test() => Ok(new { Message = "Auth Microservice is running successfully!" });

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody]RegisterDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.RegisterUnVerifiedAsync(dto, cancellationToken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "User registered! Please check email for confirmation code." });
    }

    [HttpPost("confirmregister")]
    public async Task<IActionResult> ConfirmRegister([FromBody]ConfirmRegisterDto confirmRegisterDto, CancellationToken ctoken = default)
    {
        var result = await _reglogService.ConfirmEmailAsync(confirmRegisterDto.Email, confirmRegisterDto.Code, ctoken);
        if(!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        var user = result.Data;
        string deviceId = string.IsNullOrWhiteSpace(confirmRegisterDto.DeviceId) ? "default_web_client" : confirmRegisterDto.DeviceId;
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, ctoken);
        Response.Cookies.Append("refreshToken", refreshToken, GetCookieOptions());
        return Ok(new { Message = "User verified successfully!", Token = _tokenService.GenerateToken(result.Data, deviceId) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody]LoginDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.ValidateUserForLoginAsync(dto, cancellationToken);
        if (!result.Success) return Unauthorized(new { Message = result.ErrorMessage });

        var user = result.Data;
        if (user.TwoFactorEnabled)
        {
            string ticket = Guid.NewGuid().ToString("N");
            string cacheKey = $"2fa_ticket:{ticket}";
            await _cache.SetStringAsync(cacheKey, user.Id.ToString(), new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            }, cancellationToken);

            return Ok(new
            {
                RequiresTwoFactor = true,
                TwoFactorTicket = ticket,
                Message = "Необхідна двофакторна автентифікація."
            });
        }

        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, cancellationToken);
        Response.Cookies.Append("refreshToken", refreshToken, GetCookieOptions());

        return Ok(new { Message = "User logged in successfully!", Token = _tokenService.GenerateToken(user, deviceId) });
    }

    [HttpPost("google")]
    public async Task<IActionResult> GoogleAuth([FromBody] GoogleLoginDto dto, CancellationToken ctoken)
    {
        try
        {
            string clientId = _configuration["Authentication:Google:ClientId"] ?? _configuration["GOOGLE_CLIENT_ID"] ?? "";
            string clientSecret = _configuration["Authentication:Google:ClientSecret"] ?? _configuration["GOOGLE_CLIENT_SECRET"] ?? "";

            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets 
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret
                },
                DataStore = new NullDataStore()
            });

            TokenResponse tokenResponse = await flow.ExchangeCodeForTokenAsync(
                userId: "user",
                code: dto.Code,
                redirectUri: ResolveGoogleRedirectUri(dto.RedirectUri),
                ctoken
            );

            var payload = await GoogleJsonWebSignature.ValidateAsync(tokenResponse.IdToken);
            var result = await _reglogService.LoginOrRegisterGoogleUserAsync(payload.Email, payload.Name, ctoken);
            if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

            var user = result.Data;
            string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
            string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, ctoken);
            Response.Cookies.Append("refreshToken", refreshToken, GetCookieOptions());

            return Ok(new {
                Message = "User authenticated via Google successfully!",
                Token = _tokenService.GenerateToken(user, deviceId)
            });
        }
        catch (TokenResponseException tokenEx)
        {
            return BadRequest(new { Message = "Ошибка обмена кода Google. Проверьте redirectUri.", Details = tokenEx.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = "Google Auth failed.", Details = ex.Message });
        }
    }

    private string ResolveGoogleRedirectUri(string? clientRedirectUri)
    {
        var fallback = _configuration["Authentication:Google:RedirectUri"] ?? "http://localhost:5178/auth/callback";
        if (string.IsNullOrWhiteSpace(clientRedirectUri))
            return fallback;

        var allowedOrigins = _configuration.GetSection("Authentication:Google:AllowedRedirectOrigins").Get<string[]>() ?? Array.Empty<string>();
        bool isAllowed = allowedOrigins.Any(origin =>
            string.Equals(clientRedirectUri, $"{origin.TrimEnd('/')}/auth/callback", StringComparison.OrdinalIgnoreCase));

        return isAllowed ? clientRedirectUri : fallback;
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Email))
            return Unauthorized(new { Message = "Refresh payload or email is missing." });

        if (!Request.Cookies.TryGetValue("refreshToken", out var clientToken))
            return Unauthorized(new { Message = "Refresh token cookie is missing." });

        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        var valResult = await _reglogService.ValidateRefreshTokenAsync(dto.Email, deviceId, clientToken);
        if (!valResult.Success) return Unauthorized(new { Message = valResult.ErrorMessage });

        var user = await _reglogService.FindUserByEmailAsync(dto.Email);
        if (user == null) return NotFound(new { Message = "User not found." });

        // Скользящая сессия: пока юзер активен, продлеваем refresh-токен ещё на 30 дней
        // и переустанавливаем cookie ради нового Expires.
        await _reglogService.TouchSessionAsync(dto.Email, deviceId, clientToken);
        Response.Cookies.Append("refreshToken", clientToken, GetCookieOptions());

        return Ok(new { Token = _tokenService.GenerateToken(user, deviceId) });
    }
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutDto dto, CancellationToken ctoken)
    {
        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        await _reglogService.RevokeSessionAsync(dto.Email, deviceId,ctoken);
        Response.Cookies.Delete("refreshToken", GetDeleteCookieOptions());
        return Ok(new { Message = "User logged out." });
    }

    [HttpPost("changepassword")]
    public async Task<IActionResult> ChangePassword([FromBody]ChangePasswordDto dto, CancellationToken ctoken = default)
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

    [HttpPost("revoke-all")]
    public async Task<IActionResult> RevokeAll(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized();

        var user = await _reglogService.FindUserByIdAsync(userId, false, ctoken);
        if (user == null) return NotFound();

        await _reglogService.RevokeAllSessionsAsync(user.Email, ctoken);
        Response.Cookies.Delete("refreshToken", GetDeleteCookieOptions());
        return Ok(new { Message = "Все сессии аннулированы." });
    }

    [HttpPost("revoke-session")]
    public async Task<IActionResult> RevokeSession([FromBody] RevokeSessionDto dto, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized();

        var user = await _reglogService.FindUserByIdAsync(userId, false, ctoken);
        if (user == null) return NotFound();

         await _reglogService.RevokeSessionAsync(user.Email, dto.DeviceId ?? "default_web_client", ctoken);
         return Ok(new { Message = "Сесія видалена." });
    }
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized();

        var user = await _reglogService.FindUserByIdAsync(userId, false, ctoken);
        if (user == null) return NotFound();

        var sessions = await _reglogService.GetActiveSessionsAsync(user.Email, ctoken);
        return Ok(new { Sessions = sessions });
    }

    [HttpPost("requestresetpassword")]
    public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _reglogService.FindUserByEmailAsync(dto.Email, true, cancellationToken);
        if (user == null) return Ok(new { Message = "Password reset email sent!" });
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
    [HttpGet("checkusername")]
    public async Task<IActionResult> CheckUsername([FromQuery] string username, CancellationToken ctoken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length < 3 || username.Length > 30)
            return BadRequest(new { available = false, message = "Invalid username format." });

        bool available = await _reglogService.IsUsernameAvailableAsync(username, ctoken);
        return Ok(new { available });
    }

    private bool IsSecureRequest()
    {
        if (Request.IsHttps) return true;
        if (string.Equals(Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase)) return true;
        var host = Request.Host.Host;
        if (!string.IsNullOrEmpty(host) && host != "localhost" && host != "127.0.0.1") return true;
        return false;
    }

    private CookieOptions GetCookieOptions()
    {
        bool secure = IsSecureRequest();
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/"
        };
    }

    private CookieOptions GetDeleteCookieOptions()
    {
        bool secure = IsSecureRequest();
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = secure ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/"
        };
    }
}