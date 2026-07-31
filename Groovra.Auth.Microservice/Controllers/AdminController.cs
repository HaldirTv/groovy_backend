using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Auth.Microservice.Controllers;

// Свідомо БЕЗ [Authorize]: цей сервіс не реєструє жодної authentication scheme - JWT
// перевіряє gateway і прокидає X-User-Id / X-User-Role заголовками (див. HttpContextExtensions).
// [Authorize] без default scheme кидає виняток на кожному запиті. Захист двошаровий:
// AuthorizationPolicy "AdminOnly" на маршруті gateway + EnsureAdmin() нижче.
[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly AdminService _adminService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AdminService adminService, ILogger<AdminController> logger)
    {
        _adminService = adminService;
        _logger = logger;
    }

    private bool EnsureAdmin(out IActionResult? forbidResult)
    {
        if (!HttpContext.TryGetUserId(out _))
        {
            forbidResult = Unauthorized(new { Message = "Потрібна авторизація." });
            return false;
        }
        if (!HttpContext.UserIsInRole(AppRoles.Admin))
        {
            forbidResult = StatusCode(StatusCodes.Status403Forbidden, new { Message = "Недостатньо прав." });
            return false;
        }
        forbidResult = null;
        return true;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search,
        [FromQuery] string? role,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        return Ok(await _adminService.GetUsersAsync(search, role, pageNumber, pageSize, ctoken));
    }

    [HttpGet("artist-applications")]
    public async Task<IActionResult> GetArtistApplications(
        [FromQuery] string status = "Pending", CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        return Ok(await _adminService.GetArtistApplicationsAsync(status, ctoken));
    }

    [HttpPost("artist-applications/{userId:guid}/approve")]
    public async Task<IActionResult> ApproveArtistApplication(Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.ApproveArtistApplicationAsync(userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "Заявку схвалено." });
    }

    [HttpPost("artist-applications/{userId:guid}/reject")]
    public async Task<IActionResult> RejectArtistApplication(Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.RejectArtistApplicationAsync(userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "Заявку відхилено." });
    }
    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        return Ok(await _adminService.GetRolesAsync(ctoken));
    }

    [HttpPost("users/{userId:guid}/suspend")]
    public async Task<IActionResult> ToggleSuspendUser(Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.ToggleSuspendUserAsync(userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { IsSuspended = result.Data });
    }

    [HttpPost("users/{userId:guid}/reset-password")]
    public async Task<IActionResult> ResetUserPassword(Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.ResetUserPasswordAsync(userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "Лист для скидання пароля надіслано." });
    }

    [HttpPost("users/{userId:guid}/force-logout")]
    public async Task<IActionResult> ForceLogoutUser(Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.ForceLogoutUserAsync(userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "Усі сесії користувача завершено." });
    }

    // ====== SECURITY ENDPOINTS ======

    [HttpGet("security/overview")]
    public async Task<IActionResult> GetSecurityOverview(CancellationToken ctoken)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetSecurityOverviewAsync(ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetSecurityOverview");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpGet("security/stats")]
    public async Task<IActionResult> GetSecurityStats(CancellationToken ctoken)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetSecurityStatsAsync(ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetSecurityStats");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpGet("security/login-audits")]
    public async Task<IActionResult> GetLoginAudits(
        [FromQuery] string? search,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ctoken = default)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetLoginAuditsAsync(search, pageNumber, pageSize, ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetLoginAudits");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpGet("security/threats")]
    public async Task<IActionResult> GetThreats(
        [FromQuery] string? type,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ctoken = default)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetThreatsAsync(type, pageNumber, pageSize, ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetThreats");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpGet("security/oauth-risks")]
    public async Task<IActionResult> GetOAuthRisks(
        [FromQuery] string? provider,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ctoken = default)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetOAuthRisksAsync(provider, pageNumber, pageSize, ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetOAuthRisks");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpPost("security/threats/{threatId:guid}/resolve")]
    public async Task<IActionResult> ResolveThreat(Guid threatId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.ResolveThreatAsync(threatId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });
        return Ok(new { Message = "Загрозу розв'язано." });
    }

    [HttpGet("security/threat-types")]
    public async Task<IActionResult> GetAvailableThreatTypes(CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.GetAvailableThreatTypesAsync(ctoken);
        return Ok(result);
    }

    [HttpGet("security/oauth-providers")]
    public async Task<IActionResult> GetAvailableOAuthProviders(CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        var result = await _adminService.GetAvailableOAuthProvidersAsync(ctoken);
        return Ok(result);
    }

    [HttpGet("dashboard/stats")]
    public async Task<IActionResult> GetDashboardStats(CancellationToken ctoken)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetDashboardStatsAsync(ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDashboardStats");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    [HttpGet("dashboard/activity")]
    public async Task<IActionResult> GetActivityFeed(CancellationToken ctoken)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetActivityFeedAsync(ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetActivityFeed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }

    // ====== CREATE USER ======

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequestDto dto, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return BadRequest(new { Message = "Заповніть всі обов'язкові поля." });

        var result = await _adminService.CreateUserAsync(dto.Username, dto.Email, dto.Password, dto.Role ?? AppRoles.Listener, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "Користувача створено.", UserId = result.Data });
    }

    // ====== BULK APPROVE/REJECT ARTIST APPLICATIONS ======

    [HttpPost("artist-applications/bulk-approve")]
    public async Task<IActionResult> BulkApproveArtistApplications([FromBody] BulkActionRequestDto dto, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var result = await _adminService.BulkApproveArtistApplicationsAsync(dto.UserIds, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = $"Затверджено {result.Data} заявок." });
    }

    [HttpPost("artist-applications/bulk-reject")]
    public async Task<IActionResult> BulkRejectArtistApplications([FromBody] BulkActionRequestDto dto, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var result = await _adminService.BulkRejectArtistApplicationsAsync(dto.UserIds, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = $"Відхилено {result.Data} заявок." });
    }

    // ====== ROLE MANAGEMENT ======

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequestDto dto, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { Message = "Вкажіть назву ролі." });

        var result = await _adminService.CreateRoleAsync(dto.Name, dto.Description, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "Роль створено.", RoleId = result.Data });
    }

    [HttpDelete("roles/{roleId:guid}")]
    public async Task<IActionResult> DeleteRole(Guid roleId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var result = await _adminService.DeleteRoleAsync(roleId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "Роль видалено." });
    }

    [HttpPost("roles/{roleId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> AddUserToRole(Guid roleId, Guid userId, CancellationToken ctoken)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var result = await _adminService.AddUserToRoleAsync(roleId, userId, ctoken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "Користувача додано до ролі." });
    }

    [HttpGet("roles/{roleId:guid}/capabilities")]
    public async Task<IActionResult> GetRoleCapabilities(Guid roleId, CancellationToken ctoken)
    {
        try
        {
            if (!EnsureAdmin(out var forbid)) return forbid!;
            var result = await _adminService.GetRoleCapabilitiesAsync(roleId, ctoken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetRoleCapabilities");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Internal server error", Error = ex.Message });
        }
    }
}