using System.Text.Json;
using System.Text.Json.Serialization;
using Groovra.Auth.Microservice.Data; 
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Models;
using Groovra.Shared.Constants;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;

namespace Groovra.Auth.Microservice.Services;

public class ReglogService
{
    private readonly AuthDbContext _context;
    private readonly TokenService _tokenService;
    private readonly IEmailSender _emailService;
    private readonly IDistributedCache _cache;
    private readonly IHostEnvironment _environment;

    public ReglogService(AuthDbContext context, TokenService tokenService, IEmailSender emailService, IDistributedCache cache, IHostEnvironment environment)
    {
        _context = context;
        _tokenService = tokenService;
        _emailService = emailService;
        _cache = cache;
        _environment = environment;
    }
    public async Task<ServiceResult<bool>> RegisterUnVerifiedAsync(RegisterDto registerUserDto, CancellationToken token = default)
    {
        var emailExists = await _context.Users.AnyAsync(u => u.Email == registerUserDto.Email, token);
        string pendingKey = $"pending_user:{registerUserDto.Email}";

        if (emailExists)
        {
            return ServiceResult<bool>.Fail("Email already exists");
        }
        var nicknameExist = await _context.Users.AnyAsync(u => u.Username == registerUserDto.Username, token);
        if (nicknameExist) return ServiceResult<bool>.Fail("Username already exists");

        string? existingPending = await _cache.GetStringAsync(pendingKey, token);
        if (existingPending != null)
        {
            return ServiceResult<bool>.Fail("Email already exists in pending registrations");
        }

        int code = Random.Shared.Next(100000, 999999);

        if (_environment.IsDevelopment())
        {
            Console.WriteLine($"[REGISTRATION CODE] Confirmation code for {registerUserDto.Email}: {code}");
        }

        var pendingUser = new PendingUserCacheDto()
        {
            Username = registerUserDto.Username,
            Email = registerUserDto.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(registerUserDto.Password),
            ConfirmationCode = code.ToString()
        };
        string subject = "Підтвердження реєстрації Groovra";
        string body = $"Ваш код підтвердження: {code}. Дійсний протягом 10 хвилин.";
        try
        {
            await _emailService.SendEmailAsync(BodyContent: body, Subject: subject, ToAddress: registerUserDto.Email, cancellationToken: token);
        }
        catch (Exception ex)
        {
            return ServiceResult<bool>.Fail($"Failed to send confirmation email: {ex.Message}");
        }
        await _cache.SetStringAsync(pendingKey, JsonSerializer.Serialize(pendingUser), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        }, token);

        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<User>> ConfirmEmailAsync(string email, string code, CancellationToken token = default)
    {
        string pendingKey = $"pending_user:{email}";
        string? json = await _cache.GetStringAsync(pendingKey, token);
        if (string.IsNullOrEmpty(json))
        {
            return ServiceResult<User>.Fail("No pending registration found for this email or code expired");
        }
        var emailExists = await _context.Users.AnyAsync(u => u.Email == email, token);
        if (emailExists)
        {
            await _cache.RemoveAsync(pendingKey, token);
            return ServiceResult<User>.Fail("Email already exists");
        }
        var pendingUser = JsonSerializer.Deserialize<PendingUserCacheDto>(json);
        if (pendingUser == null)
        {
            return ServiceResult<User>.Fail("Invalid pending registration data");
        }
        if (pendingUser.ConfirmationCode != code)
        {
            return ServiceResult<User>.Fail("Invalid confirmation code");
        }
        Role? listenerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Listener, token);

        var user = new User
        {
            Username = pendingUser.Username,
            Email = pendingUser.Email,
            PasswordHash = pendingUser.PasswordHash
        };

        if (listenerRole != null) user.Roles.Add(listenerRole);

        _context.Users.Add(user);
        AttachListenerProfile(_context, user);
        await _context.SaveChangesAsync(token);

        await _cache.RemoveAsync(pendingKey, token);

        return ServiceResult<User>.Ok(user);
    }

    public async Task<ServiceResult<User>> AssignArtistRoleAsync(Guid userId, CancellationToken token = default)
    {
        var user = await _context.Users
            .Include(u => u.Roles)
            .Include(u => u.ArtistProfile)
            .FirstOrDefaultAsync(u => u.Id == userId, token);

        if (user == null) return ServiceResult<User>.Fail("User not found.");

        if (!user.Roles.Any(r => r.Name == AppRoles.Artist))
        {
            Role? artistRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Artist, token);
            if (artistRole == null) return ServiceResult<User>.Fail("Artist role is not configured.");
            user.Roles.Add(artistRole);
        }

        if (user.ArtistProfile == null)
        {
            AttachArtistProfile(_context, user);
        }

        await _context.SaveChangesAsync(token);
        return ServiceResult<User>.Ok(user);
    }

    private static void AttachListenerProfile(AuthDbContext context, User user)
    {
        var profile = new Profile
        {
            UserId = user.Id,
            DisplayName = user.Username
        };
        context.Profiles.Add(profile);
        user.Profile = profile;
    }

    private static void AttachArtistProfile(AuthDbContext context, User user)
    {
        var artist = new Artist
        {
            UserId = user.Id,
            Bio = "This is the artist's bio.",
            AvatarUrl = "https://as2.ftcdn.net/v2/jpg/00/64/67/63/1000_F_64676383_LdbmhiNM6Ypzb3FM4PPuFP9rHe7ri8Ju.jpg",
            BannerUrl = string.Empty
        };
        context.Artists.Add(artist);
        user.ArtistProfile = artist;
    }

    public async Task<ServiceResult<User>> ValidateUserForLoginAsync(LoginDto loginUser, CancellationToken token = default)
    {
        var user = await _context.Users
            .Include(u => u.Roles) 
            .FirstOrDefaultAsync(u => u.Email == loginUser.Email, token);

        if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.PasswordHash))
        {
            await LogLoginAttemptAsync(
                userId: user?.Id,
                email: loginUser.Email,
                success: false,
                failureReason: user == null ? "UserNotFound" : "InvalidPassword",
                ctoken: token);

            return ServiceResult<User>.Fail("Invalid email or password.");
        }

        // Заблокований адміністратором акаунт не пускаємо далі навіть з правильним паролем.
        // Без цієї перевірки admin/users/{id}/suspend лише виставляв би прапорець у БД, ні на
        // що не впливаючи. Пишемо в аудит окремою причиною, щоб було видно в панелі безпеки.
        if (user.IsSuspended)
        {
            await LogLoginAttemptAsync(user.Id, loginUser.Email, false, "AccountSuspended", token);
            return ServiceResult<User>.Fail("Обліковий запис заблоковано адміністратором.");
        }

        await LogLoginAttemptAsync(user.Id, loginUser.Email, true, null, token);

        return ServiceResult<User>.Ok(user);
    }
    public async Task<ServiceResult<User>> LoginOrRegisterGoogleUserAsync(string email, string username, CancellationToken token = default)
    {
        var user = await _context.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Email == email, token);

        if (user != null)
        {
            // Та сама перевірка блокування, що й у звичайному логіні - інакше через Google
            // можна було б обійти suspend.
            if (user.IsSuspended)
            {
                await LogLoginAttemptAsync(user.Id, email, false, "AccountSuspended", token);
                return ServiceResult<User>.Fail("Обліковий запис заблоковано адміністратором.");
            }

            await LogLoginAttemptAsync(user.Id, email, true, null, token);
            return ServiceResult<User>.Ok(user);
        }

        Role? listenerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Listener, token);
        user = new User
        {
            Email = email,
            Username = await GenerateUniqueUsernameAsync(username, token),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))
        };

        if (listenerRole != null)
        {
            user.Roles.Add(listenerRole);
        }

        _context.Users.Add(user);
        AttachListenerProfile(_context, user);
        await _context.SaveChangesAsync(token);

        return ServiceResult<User>.Ok(user);
    }
    private async Task<string> GenerateUniqueUsernameAsync(string baseName, CancellationToken token)
    {
        var translitMap = new Dictionary<char, string>
        {
            {'а',"a"},{'б',"b"},{'в',"v"},{'г',"g"},{'д',"d"},{'е',"e"},{'ё',"yo"},
            {'ж',"zh"},{'з',"z"},{'и',"i"},{'й',"y"},{'к',"k"},{'л',"l"},{'м',"m"},
            {'н',"n"},{'о',"o"},{'п',"p"},{'р',"r"},{'с',"s"},{'т',"t"},{'у',"u"},
            {'ф',"f"},{'х',"kh"},{'ц',"ts"},{'ч',"ch"},{'ш',"sh"},{'щ',"shch"},
            {'ъ',""},{'ы',"y"},{'ь',""},{'э',"e"},{'ю',"yu"},{'я',"ya"},
            {'і',"i"},{'ї',"yi"},{'є',"ye"},{'ґ',"g"},
        };

        var transliterated = new System.Text.StringBuilder();
        foreach (char c in baseName.ToLowerInvariant())
        {
            if (translitMap.TryGetValue(c, out string? mapped))
                transliterated.Append(mapped);
            else
                transliterated.Append(c);
        }

        string clean = new string(
            transliterated.ToString()
                .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                .ToArray()
        );

        clean = clean.Length > 40 ? clean[..40] : clean;
        if (string.IsNullOrEmpty(clean)) clean = "user";

        string candidate = clean;
        int counter = 1;
        while (await _context.Users.AnyAsync(u => u.Username == candidate, token))
        {
            candidate = $"{clean}{counter}";
            counter++;
        }

        return candidate;
    }
    public async Task<ServiceResult<bool>> ChangePasswordAsync(User user, string OldPassword, string NewPassword, CancellationToken token = default)
    {
        if (!BCrypt.Net.BCrypt.Verify(OldPassword, user.PasswordHash))
        {
            return ServiceResult<bool>.Fail("Текущий пароль неверный.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        await _context.SaveChangesAsync(token);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> ChangePasswordAsync(User user, string NewPassword, CancellationToken token = default)
    {
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        await _context.SaveChangesAsync(token);

        return ServiceResult<bool>.Ok(true);
    }
    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken token = default)
    {
        return !await _context.Users.AnyAsync(u => u.Username == username, token);
    }
    public async Task<ServiceResult<string>> RequestPasswordResetAsync(User user, CancellationToken cancellationToken)
    {
        int number = Random.Shared.Next(100000, 999999);
        string BodyContent = $"Ваш код {number} введіть на сайті для підтвердження";
        string Subject = "Сброс пароля в Groovra";
        try
        {
            await _emailService.SendEmailAsync(BodyContent: BodyContent, Subject: Subject, ToAddress: user.Email, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return ServiceResult<string>.Fail($"Failed to send reset email: {ex.Message}");
        }

        return ServiceResult<string>.Ok(number.ToString());
    }

    public async Task RevokeAllSessionsAsync(string email, CancellationToken ctoken = default)
    {
        string devicesKey = $"user_devices:{email}";
        string? json = await _cache.GetStringAsync(devicesKey, ctoken);
        if (string.IsNullOrEmpty(json)) return; 

        List<string> devices = JsonSerializer.Deserialize<List<string>>(json) ?? new();
        if (devices.Count == 0) return;
        foreach (string device in devices)
        {
            string deviceCacheKey = $"refresh:{email}:{device}";
            await _cache.RemoveAsync(deviceCacheKey, ctoken);
        }
        await _cache.RemoveAsync(devicesKey, ctoken);
    }

    public async Task RevokeSessionAsync(string email,string deviceId,CancellationToken ctoken = default)
    {
        string devicesKey = $"user_devices:{email}";
        string? json = await _cache.GetStringAsync(devicesKey, ctoken);
        if (!string.IsNullOrEmpty(json))
        {
            var devices = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            devices.Remove(deviceId);
            await _cache.SetStringAsync(devicesKey, JsonSerializer.Serialize(devices), ctoken);
        }
        await _cache.RemoveAsync($"refresh:{email}:{deviceId}", ctoken);
    }
    public async Task<string> CreateSessionAsync(string email, string deviceId = "default_web_client", CancellationToken token = default)
    {
        string refreshToken = Guid.NewGuid().ToString("N");
        string cacheKey = $"refresh:{email}:{deviceId}";
        string devicesKey = $"user_devices:{email}";

        await _cache.SetStringAsync(cacheKey, refreshToken, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
        }, token);

        string? json = await _cache.GetStringAsync(devicesKey, token);
        List<string> devices = json != null 
            ? JsonSerializer.Deserialize<List<string>>(json) 
            : new List<string>();

        if (!devices.Contains(deviceId))
        {
            devices.Add(deviceId);
            await _cache.SetStringAsync(devicesKey, JsonSerializer.Serialize(devices), token);
        }

        return refreshToken;
    }

    /// <summary>Продлевает жизнь существующей сессии на свежие 30 дней без смены значения
    /// токена (скользящее окно) — вызывается из /auth/refresh, пока пользователь активен.</summary>
    public async Task TouchSessionAsync(string email, string deviceId, string refreshToken, CancellationToken token = default)
    {
        string cacheKey = $"refresh:{email}:{deviceId}";
        await _cache.SetStringAsync(cacheKey, refreshToken, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
        }, token);
    }

    public async Task<List<string>> GetActiveSessionsAsync(string email, CancellationToken ctoken = default)
    {
        string devicesKey = $"user_devices:{email}";
        string? json = await _cache.GetStringAsync(devicesKey, ctoken);
        if (string.IsNullOrEmpty(json)) return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(json) ?? new();
    }
   public async Task<ServiceResult<string>> ValidateRefreshTokenAsync(string email, string deviceId, string clientToken)
    {
        string cacheKey = $"refresh:{email}:{deviceId}";
        string? savedToken = await _cache.GetStringAsync(cacheKey);
        if (string.IsNullOrEmpty(savedToken) || savedToken != clientToken)
            return ServiceResult<string>.Fail("Invalid or expired refresh token.");
        return ServiceResult<string>.Ok(savedToken);
    }
   public async Task SaveResetCodeAsync(string email, string code)
    {
        await _cache.SetStringAsync($"password_reset:{email}", code, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }
public async Task<bool> VerifyResetCodeAsync(string email, string code)
{
    string key = $"password_reset:{email}";
    string? saved = await _cache.GetStringAsync(key);
    if (saved == null || saved != code) return false;
    await _cache.RemoveAsync(key);
    return true;
}

public async Task SaveVerifiedTokenAsync(string email, string token)
{
    await _cache.SetStringAsync($"verified_token:{email}", token, new DistributedCacheEntryOptions 
    { 
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) 
    });
}

public async Task<bool> VerifyResetTokenAsync(string email, string token)
{
    string key = $"verified_token:{email}";
    string? saved = await _cache.GetStringAsync(key);
    if (saved == null || saved != token) return false;
    await _cache.RemoveAsync(key);
    return true;
}
    public async Task<User?> FindUserByIdAsync(Guid userId, bool loadReference = true, CancellationToken token = default)
    {
        IQueryable<User> query = _context.Users;
        if (loadReference) query = query.Include(u => u.Roles);
        return await query.FirstOrDefaultAsync(u => u.Id == userId, token);
    }

    public async Task<User?> FindUserByEmailAsync(string email, bool loadReference = true, CancellationToken cancellationToken = default)
    {
        IQueryable<User> query = _context.Users;
        if (loadReference) query = query.Include(u => u.Roles);
        return await query.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
    }

    /// <summary>Пише спробу входу в auth.LoginAudits для адмінської панелі безпеки.
    /// Свідомо ковтає власні винятки: збій аудиту не має ламати сам логін.
    /// IpAddress тут завжди null - сервіс не має доступу до HttpContext; IP проставляє
    /// контролер там, де він потрібен.</summary>
    private async Task LogLoginAttemptAsync(Guid? userId, string email, bool success,
        string? failureReason, CancellationToken ctoken = default)
    {
        try
        {
            _context.LoginAudits.Add(new LoginAudit
            {
                UserId = userId,
                Email = email,
                Success = success,
                IpAddress = null,
                FailureReason = failureReason,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(ctoken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Failed to log login attempt: {ex.Message}");
        }
    }
}