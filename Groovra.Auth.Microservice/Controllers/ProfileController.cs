using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Models;
using Groovra.Messaging.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("profile")]
public class  ProfileController : ControllerBase
{
    private const long MaxImageFileSizeBytes = 10L * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/jpg",
    };
    private readonly AuthDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<ProfileController> _logger;
    private readonly string _mediaBasePath;

    public ProfileController(AuthDbContext db, IPublishEndpoint publishEndpoint, IConfiguration configuration, ILogger<ProfileController> logger)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
        _logger = logger;

        // AppContext.BaseDirectory, а не текущий каталог процесса — иначе аватар,
        // сохранённый при одном способе запуска, не находился бы при другом.
        var configured = configuration["MediaStorage:BasePath"];
        var basePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
            : Path.GetFullPath(configured);

        try
        {
            Directory.CreateDirectory(Path.Combine(basePath, "avatars"));
            Directory.CreateDirectory(Path.Combine(basePath, "banners"));
            _mediaBasePath = basePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create media path at {BasePath}, falling back to TempPath", basePath);
            _mediaBasePath = Path.Combine(Path.GetTempPath(), "MediaStorage");
            try
            {
                Directory.CreateDirectory(Path.Combine(_mediaBasePath, "avatars"));
                Directory.CreateDirectory(Path.Combine(_mediaBasePath, "banners"));
            }
            catch { }
        }

    }

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        var profile = await _db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ctoken);

        var res = profile is null
            ? new ProfileResponseDto { DisplayName = user?.Username ?? string.Empty, Username = user?.Username ?? string.Empty }
            : MapToDto(profile, user?.Username);
        res.CreatedAt = user?.CreatedAt;
        res.UserId = userId;
        res.FollowersCount = await _db.UserFollows.CountAsync(f => f.FollowedId == userId, ctoken);
        res.FollowingCount = await _db.UserFollows.CountAsync(f => f.FollowerId == userId, ctoken);
        return Ok(res);
    }

    [HttpPut]
    public async Task<IActionResult> Updaterofile([FromBody] UpdateProfileDto dto, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var profile = await GetOrCreateProfileAsync(userId, ctoken);

        var previousDisplayName = profile.DisplayName;
        if (dto.DisplayName is not null) profile.DisplayName = dto.DisplayName.Trim();
        if (dto.FirstName is not null) profile.FirstName = dto.FirstName.Trim();
        if (dto.LastName is not null) profile.LastName = dto.LastName.Trim();
        if (dto.Bio is not null) profile.Bio = dto.Bio.Trim();
        if (dto.City is not null) profile.City = dto.City.Trim();
        if (dto.Country is not null) profile.Country = dto.Country.Trim();
        if (dto.Phone is not null) profile.Phone = dto.Phone.Trim();
        if (dto.Birthday is not null) profile.Birthday = dto.Birthday.Trim();
        if (dto.Gender is not null) profile.Gender = dto.Gender.Trim();
        if (dto.LinkUrl is not null) profile.LinkUrl = dto.LinkUrl.Trim();
        if (dto.LinkLabel is not null) profile.LinkLabel = dto.LinkLabel.Trim();
        if (dto.SupportLink is not null) profile.SupportLink = dto.SupportLink.Trim();
        if (dto.SettingsJson is not null) profile.SettingsJson = dto.SettingsJson.Trim();

        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);

        if (!string.Equals(previousDisplayName, profile.DisplayName, StringComparison.Ordinal))
        {
            await _publishEndpoint.Publish(new UserNicknameChangedEvent(userId, profile.DisplayName, profile.UpdatedAt), ctoken);
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        var res = MapToDto(profile, user?.Username);
        res.CreatedAt = user?.CreatedAt;
        return Ok(res);
    }

    [HttpPost("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });
        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "File is empty." });
        if (file.Length > MaxImageFileSizeBytes)
            return BadRequest(new { Message = "Image exceeds maximum allowed size of 10 MB." });
        if (!AllowedImageMimeTypes.Contains(file.ContentType))
            return BadRequest(new { Message = $"Unsupported image format '{file.ContentType}'." });

        var profile = await GetOrCreateProfileAsync(userId, ctoken);
        DeleteFileIfExists(profile.AvatarUrl);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{userId}_avatar{ext}";
        var absolutePath = Path.Combine(_mediaBasePath, "avatars", fileName);
        await SaveFileAtomicAsync(file, absolutePath, ctoken);

        profile.AvatarUrl = $"/media/avatars/{fileName}";
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);

        _logger.LogInformation("Avatar uploaded for UserId={UserId}", userId);
        return Ok(new { AvatarUrl = profile.AvatarUrl });

    }

    [HttpPost("banner")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadBanner([FromForm] IFormFile file, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "File is empty." });

        if (file.Length > MaxImageFileSizeBytes)
            return BadRequest(new { Message = "Image exceeds maximum allowed size of 10 MB." });

        if (!AllowedImageMimeTypes.Contains(file.ContentType))
            return BadRequest(new { Message = $"Unsupported image format '{file.ContentType}'." });

        var profile = await GetOrCreateProfileAsync(userId, ctoken);
        DeleteFileIfExists(profile.BannerUrl);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{userId}_banner{ext}";
        var absolutePath = Path.Combine(_mediaBasePath, "banners", fileName);
        await SaveFileAtomicAsync(file, absolutePath, ctoken);

        profile.BannerUrl = $"/media/banners/{fileName}";
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);

        _logger.LogInformation("Banner uploaded for UserId={UserId}", userId);
        return Ok(new { BannerUrl = profile.BannerUrl });
    }

    [HttpDelete("avatar")]
    public async Task<IActionResult> DeleteAvatar(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile is null) return NotFound();
        DeleteFileIfExists(profile.AvatarUrl);
        profile.AvatarUrl = string.Empty;
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);
        return Ok(new { Message = "Avatar removed." });
    }

    [HttpDelete("banner")]
    public async Task<IActionResult> DeleteBanner(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile is null) return NotFound();
        DeleteFileIfExists(profile.BannerUrl);
        profile.BannerUrl = string.Empty;
        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);
        return Ok(new { Message = "Banner removed." });
    }

    [HttpGet("artist/status")]
    public async Task<IActionResult> GetArtistStatus(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var artist = await _db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ctoken);
        var user = await _db.Users.Include(u => u.Roles).AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        var isArtist = artist is not null || (user?.Roles.Any(r => r.Name == "Artist" || r.Name == "Admin") ?? false);

        return Ok(new
        {
            IsArtist = isArtist,
            Bio = artist?.Bio ?? string.Empty,
            CreatedAt = artist?.CreatedAt
        });
    }

    [HttpPost("artist/apply")]
    public async Task<IActionResult> ApplyArtist([FromBody] ApplyArtistDto dto, CancellationToken ctoken)
    {
        try
        {
            if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
                return Unauthorized(new { Message = "User ID invalid or missing." });

            if (string.IsNullOrWhiteSpace(dto.ArtistName))
                return BadRequest(new { Message = "Будь ласка, вкажіть ім'я виконавця." });

            var user = await _db.Users
                .Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.Id == userId, ctoken);

            if (user is null)
                return NotFound(new { Message = "Користувача не знайдено." });

            var existingArtist = await _db.Artists.FirstOrDefaultAsync(a => a.UserId == userId, ctoken);
            if (existingArtist is null)
            {
                existingArtist = new Artist
                {
                    UserId = userId,
                    Bio = $"Genre: {dto.Genre?.Trim()} | Platform: {dto.Platform?.Trim()} | Country: {dto.Country?.Trim()}",
                    CreatedAt = DateTime.UtcNow
                };
                _db.Artists.Add(existingArtist);
            }
            else
            {
                existingArtist.Bio = $"Genre: {dto.Genre?.Trim()} | Platform: {dto.Platform?.Trim()} | Country: {dto.Country?.Trim()}";
            }

            var artistRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "Artist", ctoken);
            if (artistRole is not null && !user.Roles.Any(r => r.Id == artistRole.Id))
            {
                user.Roles.Add(artistRole);
            }

            var profile = await GetOrCreateProfileAsync(userId, ctoken);
            var previousDisplayName = profile.DisplayName;
            if (!string.IsNullOrWhiteSpace(dto.ArtistName))
            {
                profile.DisplayName = dto.ArtistName.Trim();
            }
            profile.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ctoken);

            if (!string.Equals(previousDisplayName, profile.DisplayName, StringComparison.Ordinal))
            {
                await _publishEndpoint.Publish(new UserNicknameChangedEvent(userId, profile.DisplayName, profile.UpdatedAt), ctoken);
            }

            return Ok(new
            {
                Message = "Вітаємо! Вашу заявку схвалено. Вам надано статус підтвердженого автора Groovra Creator.",
                IsArtist = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process ApplyArtist");
            return StatusCode(500, new { Message = $"Не вдалося подати заявку: {ex.Message}" });
        }
    }

    [HttpGet("equalizer")]
    public async Task<IActionResult> GetEqualizerSettings(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Ok(new { isEnabled = true, activePreset = "flat", gains = new double[] { 0, 0, 0, 0, 0 } });

        var profile = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.SettingsJson))
        {
            return Ok(new { isEnabled = true, activePreset = "flat", gains = new double[] { 0, 0, 0, 0, 0 } });
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(profile.SettingsJson);
            if (doc.RootElement.TryGetProperty("equalizer", out var eqElem))
            {
                return Content(eqElem.GetRawText(), "application/json");
            }
        }
        catch
        {
        }

        return Ok(new { isEnabled = true, activePreset = "flat", gains = new double[] { 0, 0, 0, 0, 0 } });
    }

    [HttpPut("equalizer")]
    public async Task<IActionResult> UpdateEqualizerSettings([FromBody] System.Text.Json.JsonElement eqBody, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Ok(eqBody);

        var profile = await GetOrCreateProfileAsync(userId, ctoken);

        System.Text.Json.Nodes.JsonObject rootObj;
        try
        {
            rootObj = string.IsNullOrWhiteSpace(profile.SettingsJson)
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonNode.Parse(profile.SettingsJson)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch
        {
            rootObj = new System.Text.Json.Nodes.JsonObject();
        }

        rootObj["equalizer"] = System.Text.Json.Nodes.JsonNode.Parse(eqBody.GetRawText());
        profile.SettingsJson = rootObj.ToJsonString();
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ctoken);
        return Content(eqBody.GetRawText(), "application/json");
    }

    private async Task<Profile> GetOrCreateProfileAsync(Guid userId, CancellationToken ctoken)
    {
        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile is not null) return profile;

        var username = await _db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(ctoken);
        profile = new Profile { UserId = userId, DisplayName = username ?? string.Empty };
        _db.Profiles.Add(profile);
        return profile;
    }

    private void DeleteFileIfExists(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        var relativePath = url.TrimStart('/').Replace("media/", "");
        var fullPath = Path.Combine(_mediaBasePath, relativePath);
        if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
    }

    private static async Task SaveFileAtomicAsync(IFormFile file, string destinationPath, CancellationToken ctoken)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            await using var tempStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81_920, useAsync: true);
            await file.CopyToAsync(tempStream, ctoken);
        }
        catch
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
            throw;
        }
        System.IO.File.Move(tempPath, destinationPath, overwrite: true);
    }

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> GetProfileById(Guid userId, CancellationToken ctoken)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ctoken);
        if (user is null)
            return NotFound(new { Message = "User not found." });

        var profile = await _db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ctoken);

        var res = profile is null ? new ProfileResponseDto { DisplayName = user.Username, Username = user.Username } : MapToDto(profile, user.Username);
        res.CreatedAt = user?.CreatedAt;
        res.UserId = userId;
        res.FollowersCount = await _db.UserFollows.CountAsync(f => f.FollowedId == userId, ctoken);
        res.FollowingCount = await _db.UserFollows.CountAsync(f => f.FollowerId == userId, ctoken);
        return Ok(res);
    }

    [HttpGet("by-name/{displayName}")]
    public async Task<IActionResult> GetProfileByDisplayName(string displayName, CancellationToken ctoken)
    {
        var decodedName = Uri.UnescapeDataString(displayName).Trim();
        var profile = await _db.Profiles
            .Include(p => p.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.DisplayName.ToLower() == decodedName.ToLower(), ctoken);

        if (profile is null)
        {
            var user = await _db.Users
                .Include(u => u.Profile)
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username.ToLower() == decodedName.ToLower(), ctoken);

            if (user is null)
                return NotFound(new { Message = "Profile not found." });

            var fallbackRes = user.Profile is null ? new ProfileResponseDto { DisplayName = user.Username, Username = user.Username } : MapToDto(user.Profile, user.Username);
            fallbackRes.CreatedAt = user.CreatedAt;
            fallbackRes.UserId = user.Id;
            fallbackRes.FollowersCount = await _db.UserFollows.CountAsync(f => f.FollowedId == user.Id, ctoken);
            fallbackRes.FollowingCount = await _db.UserFollows.CountAsync(f => f.FollowerId == user.Id, ctoken);
            return Ok(fallbackRes);
        }

        var res = MapToDto(profile, profile.User?.Username);
        res.CreatedAt = profile.User?.CreatedAt;
        res.UserId = profile.UserId;
        res.FollowersCount = await _db.UserFollows.CountAsync(f => f.FollowedId == profile.UserId, ctoken);
        res.FollowingCount = await _db.UserFollows.CountAsync(f => f.FollowerId == profile.UserId, ctoken);
        return Ok(res);
    }

    [HttpPost("follow/{followedId:guid}")]
    public async Task<IActionResult> FollowProfile(Guid followedId, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid followerId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        if (followerId == followedId)
            return BadRequest(new { Message = "You cannot subscribe to yourself." });

        var targetUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == followedId, ctoken);
        if (targetUser is null)
            return NotFound(new { Message = "Target user not found." });

        var alreadyFollowing = await _db.UserFollows
            .AnyAsync(f => f.FollowerId == followerId && f.FollowedId == followedId, ctoken);

        if (!alreadyFollowing)
        {
            var follow = new UserFollow
            {
                FollowerId = followerId,
                FollowedId = followedId,
                FollowedAt = DateTime.UtcNow
            };
            _db.UserFollows.Add(follow);
            await _db.SaveChangesAsync(ctoken);
        }

        return Ok(new { Message = "Successfully subscribed." });
    }

    [HttpDelete("follow/{followedId:guid}")]
    public async Task<IActionResult> UnfollowProfile(Guid followedId, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid followerId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var follow = await _db.UserFollows
            .FirstOrDefaultAsync(f => f.FollowerId == followerId && f.FollowedId == followedId, ctoken);

        if (follow is not null)
        {
            _db.UserFollows.Remove(follow);
            await _db.SaveChangesAsync(ctoken);
        }

        return Ok(new { Message = "Successfully unsubscribed." });
    }

    [HttpGet("follow/status/{followedId:guid}")]
    public async Task<IActionResult> GetFollowStatus(Guid followedId, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid followerId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var isFollowing = await _db.UserFollows
            .AnyAsync(f => f.FollowerId == followerId && f.FollowedId == followedId, ctoken);

        return Ok(new { IsFollowing = isFollowing });
    }

    [HttpGet("following")]
    public async Task<IActionResult> GetFollowingList(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid followerId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var followedUsers = await _db.UserFollows
            .Where(f => f.FollowerId == followerId)
            .Include(f => f.Followed)
                .ThenInclude(u => u.Profile)
            .Select(f => f.Followed)
            .AsNoTracking()
            .ToListAsync(ctoken);

        var list = followedUsers.Select(u =>
        {
            var dto = u.Profile is null ? new ProfileResponseDto { DisplayName = u.Username, Username = u.Username } : MapToDto(u.Profile, u.Username);
            dto.CreatedAt = u.CreatedAt;
            dto.UserId = u.Id;
            return dto;
        }).ToList();

        return Ok(list);
    }

    private static ProfileResponseDto MapToDto(Profile p, string? usernameFallback = null) => new()
    {
        UserId = p.UserId,
        Username = usernameFallback ?? string.Empty,
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? (usernameFallback ?? p.DisplayName) : p.DisplayName,
        FirstName = p.FirstName,
        LastName = p.LastName,
        Bio = p.Bio,
        City = p.City,
        Country = p.Country,
        Phone = p.Phone,
        Birthday = p.Birthday,
        Gender = p.Gender,
        AvatarUrl = p.AvatarUrl,
        BannerUrl = p.BannerUrl,
        LinkUrl = p.LinkUrl,
        LinkLabel = p.LinkLabel,
        SupportLink = p.SupportLink,
        SettingsJson = p.SettingsJson
    };
}

