using System.Text.Json;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2.Flows;
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Mvc;
using Groovra.Shared.ServiceResult;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2; 
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly ReglogService _reglogService;
    private readonly TokenService _tokenService;
    private readonly IConfiguration _configuration;
    public AuthController(ReglogService reglogService, TokenService tokenService, IConfiguration configuration)   
    {
        _reglogService = reglogService;
        _tokenService = tokenService;
        _configuration = configuration;
        
    }

    [HttpGet("test")]
    public IActionResult Test() => Ok(new { Message = "Auth Microservice is running successfully!" });

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody]RegisterDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.RegisterUnVerifiedAsync(dto, cancellationToken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "User registered successfully! You have 10 minutes to verify." });
    }

    [HttpPost("confirmregister")]
    public async Task<IActionResult> ConfirmRegister([FromBody]ConfirmRegisterDto confirmRegisterDto, CancellationToken ctoken = default)
    {
        var result = await _reglogService.ConfirmEmailAsync(confirmRegisterDto.Email, confirmRegisterDto.Code, ctoken);
        if(!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        
        var user = result.Data;
        string deviceId = string.IsNullOrWhiteSpace(confirmRegisterDto.DeviceId) ? "default_web_client" : confirmRegisterDto.DeviceId;
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, ctoken);

        AppendRefreshTokenCookie(refreshToken);

        return Ok(new { Message = "User verified successfully!", Token = _tokenService.GenerateToken(result.Data) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody]LoginDto dto, CancellationToken cancellationToken = default)
    {
        var result = await _reglogService.ValidateUserForLoginAsync(dto, cancellationToken);
        if (!result.Success) return Unauthorized(new { Message = result.ErrorMessage });

        var user = result.Data;
        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        string refreshToken = await _reglogService.CreateSessionAsync(user.Email, deviceId, cancellationToken);

        AppendRefreshTokenCookie(refreshToken);

        return Ok(new { Message = "User logged in successfully!", Token = _tokenService.GenerateToken(user) });
    }

    [HttpPost("google")]
    public async Task<IActionResult> GoogleAuth([FromBody] GoogleLoginDto dto, CancellationToken ctoken)
    {
        try
        {
            
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                
                ClientSecrets = new ClientSecrets 
                {
                    ClientId = _configuration["Authentication:Google:ClientId"],
                    ClientSecret = _configuration["Authentication:Google:ClientSecret"]
                }
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

            AppendRefreshTokenCookie(refreshToken);

            return Ok(new {
                Message = "User authenticated via Google successfully!",
                Token = _tokenService.GenerateToken(user)
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

    /// <summary>
    /// The redirect_uri used for Google's token exchange must exactly match the one the
    /// browser was actually sent to (which varies by frontend origin/environment). The client
    /// reports the redirect_uri it used; we only trust it if its origin is on the configured
    /// allow-list, otherwise we fall back to the static configured value.
    /// </summary>
    private string ResolveGoogleRedirectUri(string? clientRedirectUri)
    {
        var fallback = _configuration["Authentication:Google:RedirectUri"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientRedirectUri))
            return fallback;

        var allowedOrigins = _configuration.GetSection("Authentication:Google:AllowedRedirectOrigins").Get<string[]>() ?? Array.Empty<string>();
        bool isAllowed = allowedOrigins.Any(origin =>
            string.Equals(clientRedirectUri, $"{origin.TrimEnd('/')}/auth/callback", StringComparison.OrdinalIgnoreCase));

        return isAllowed ? clientRedirectUri : fallback;
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto, CancellationToken ctoken = default)
    {
        if (!Request.Cookies.TryGetValue("refreshToken", out var clientToken))
            return Unauthorized(new { Message = "Refresh token cookie is missing." });

        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        var valResult = await _reglogService.ValidateRefreshTokenAsync(dto.Email, deviceId, clientToken);
        if (!valResult.Success) return Unauthorized(new { Message = valResult.ErrorMessage });

        var user = await _reglogService.FindUserByEmailAsync(dto.Email);
        if (user == null) return NotFound(new { Message = "User not found." });

        // Скользящая сессия: пока юзер активен (браузер тихо рефрешит токен), продлеваем
        // refresh-токен и cookie ещё на 30 дней. Значение токена то же — cookie переустанавливаем
        // лишь ради нового Expires, чтобы окно отсчитывалось от активности, а не от логина.
        await _reglogService.TouchSessionAsync(dto.Email, deviceId, clientToken, ctoken);
        AppendRefreshTokenCookie(clientToken);

        return Ok(new { Token = _tokenService.GenerateToken(user) });
    }

    /// <summary>
    /// Единая точка установки refresh-cookie (login / confirmregister / google / sliding refresh) —
    /// HttpOnly + Secure + SameSite=None (нужно для кросс-схемного localhost http→https) и persistent
    /// Expires на 30 дней, чтобы cookie переживала перезапуск браузера.
    /// </summary>
    private void AppendRefreshTokenCookie(string refreshToken)
    {
        Response.Cookies.Append("refreshToken", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
    }
    
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutDto dto, CancellationToken ctoken)
    {
        string deviceId = string.IsNullOrWhiteSpace(dto.DeviceId) ? "default_web_client" : dto.DeviceId;
        await _reglogService.RevokeSessionAsync(dto.Email, deviceId,ctoken);
        Response.Cookies.Delete("refreshToken", new CookieOptions 
        { 
            HttpOnly = true, 
            Secure = true, 
            SameSite = SameSiteMode.None,
            Path = "/"
        });
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
        Response.Cookies.Delete("refreshToken", new CookieOptions 
        { 
            HttpOnly = true, 
            Secure = true, 
            SameSite = SameSiteMode.None,
            Path = "/"
        });
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

        // Такая же проверка как в RegisterUnVerifiedAsync
        bool available = await _reglogService.IsUsernameAvailableAsync(username, ctoken);
        return Ok(new { available });
    }
    
}