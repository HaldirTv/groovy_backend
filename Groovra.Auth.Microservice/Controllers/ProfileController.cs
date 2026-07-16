using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Models;
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
    private readonly ILogger<ProfileController> _logger;
    private readonly string _mediaBasePath;

    public ProfileController(AuthDbContext db, IConfiguration configuration, ILogger<ProfileController> logger)
    {
        _db = db;
        _logger = logger;

        var configured = configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "avatars"));
        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "banners"));
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var profile = await _db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ctoken);

        if (profile is null)
            return Ok(new ProfileResponseDto());
        return Ok(profile);
    }

    [HttpPut]
    public async Task<IActionResult> Updaterofile([FromBody] UpdateProfileDto dto, CancellationToken ctoken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var profile = await GetOrCreateProfileAsync(userId, ctoken);

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

        profile.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ctoken);
        return Ok(MapToDto(profile));
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

    private async Task<Profile> GetOrCreateProfileAsync(Guid userId, CancellationToken ctoken)
    {
        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ctoken);
        if (profile is not null) return profile;

        profile = new Profile { UserId = userId };
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

    private static ProfileResponseDto MapToDto(Profile p) => new()
    {
        DisplayName = p.DisplayName,
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
        SupportLink = p.SupportLink
    };
}


