using System.Text.Json;
using System.Text.Json.Serialization;
using Groovra.Auth.Microservice.Data; 
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Models;
using Groovra.Shared.Constants;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Groovra.Auth.Microservice.Services;

public class ReglogService
{
    private readonly AuthDbContext _context;
    private readonly TokenService _tokenService;
    private readonly EmailService _emailService;
    private readonly IDistributedCache _cache;

    public ReglogService(AuthDbContext context, TokenService tokenService, EmailService emailService, IDistributedCache cache)
    {
        _context = context; 
        _tokenService = tokenService;
        _emailService = emailService;
        _cache = cache;
    }
    //Register/Login
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
        
        
        string? existing = await _cache.GetStringAsync(pendingKey, token);
        if (existing != null)
        {
            return ServiceResult<bool>.Fail("Email already exists in pending registrations");
        }
        
        int code = Random.Shared.Next(100000, 999999);
        var pendingUser = new PendingUserCacheDto()
        {
            Username = registerUserDto.Username,
            Email = registerUserDto.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(registerUserDto.Password),
            Role = registerUserDto.Role,
            ConfirmationCode = code.ToString()
        };
        
        string subject = "Підтвердження реєстрації Groovra";
        string body = $"Ваш код підтвердження: {code}. Дійсний протягом 10 хвилин.";
        
        try
        {
            await _emailService.sendEmailAsync(BodyContent: body, Subject: subject, ToAddress: registerUserDto.Email);
        }
        catch (Exception ex)
        {
            // Если почта не ушла, ничего в кэш не пишем
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
    
        bool isArtist = string.Equals(pendingUser.Role, AppRoles.Artist, StringComparison.OrdinalIgnoreCase);;
        List<Role> roles = new();
    
        if (isArtist)
        {
            Role? artistRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Artist, token);
            if (artistRole != null) roles.Add(artistRole);
        }
    
        Role? listenerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Listener, token);
        if (listenerRole != null) roles.Add(listenerRole);
        
        var user = new User
        {
            Username = pendingUser.Username,
            Email = pendingUser.Email,
            PasswordHash = pendingUser.PasswordHash
        };
    
        user.Roles.AddRange(roles);
    
        if (isArtist)
        {
            user.ArtistProfile = new Artist
            {
                UserId = user.Id,
                Bio = "This is the artist's bio.",
                AvatarUrl = "https://as2.ftcdn.net/v2/jpg/00/64/67/63/1000_F_64676383_LdbmhiNM6Ypzb3FM4PPuFP9rHe7ri8Ju.jpg",
                BannerUrl = string.Empty
            };
        }
    
        _context.Users.Add(user);
        await _context.SaveChangesAsync(token);
        
        await _cache.RemoveAsync(pendingKey, token);
        
        return ServiceResult<User>.Ok(user);
    }

    public async Task<ServiceResult<User>> ValidateUserForLoginAsync(LoginDto loginUser, CancellationToken token = default)
    {
        var user = await _context.Users
            .Include(u => u.Roles) 
            .FirstOrDefaultAsync(u => u.Email == loginUser.Email, token);

        if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.PasswordHash))
        {
            return ServiceResult<User>.Fail("Invalid email or password.");
        }
    
        return ServiceResult<User>.Ok(user); 
    }
    //----------------------------------------
    
    //Google oauth2
    public async Task<ServiceResult<User>> LoginOrRegisterGoogleUserAsync(string email, string username, CancellationToken token = default)
    {
  
        var user = await _context.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Email == email, token);

        if (user != null)
        {
      
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
        await _context.SaveChangesAsync(token);

        return ServiceResult<User>.Ok(user);
    }
    private async Task<string> GenerateUniqueUsernameAsync(string baseName, CancellationToken token)
    {
        // Транслітерація кирилиці в латиницю
        var translitMap = new Dictionary<char, string>
        {
            {'а',"a"},{'б',"b"},{'в',"v"},{'г',"g"},{'д',"d"},{'е',"e"},{'ё',"yo"},
            {'ж',"zh"},{'з',"z"},{'и',"i"},{'й',"y"},{'к',"k"},{'л',"l"},{'м',"m"},
            {'н',"n"},{'о',"o"},{'п',"p"},{'р',"r"},{'с',"s"},{'т',"t"},{'у',"u"},
            {'ф',"f"},{'х',"kh"},{'ц',"ts"},{'ч',"ch"},{'ш',"sh"},{'щ',"shch"},
            {'ъ',""},{'ы',"y"},{'ь',""},{'э',"e"},{'ю',"yu"},{'я',"ya"},
            // Українські
            {'і',"i"},{'ї',"yi"},{'є',"ye"},{'ґ',"g"},
        };

        // 1. Транслітеруємо
        var transliterated = new System.Text.StringBuilder();
        foreach (char c in baseName.ToLowerInvariant())
        {
            if (translitMap.TryGetValue(c, out string? mapped))
                transliterated.Append(mapped);
            else
                transliterated.Append(c);
        }

        // 2. Залишаємо тільки a-z, 0-9, підкреслення
        string clean = new string(
            transliterated.ToString()
                .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                .ToArray()
        );

        // 3. Якщо після очищення нічого не залишилось — fallback
        clean = clean.Length > 40 ? clean[..40] : clean;
        if (string.IsNullOrEmpty(clean)) clean = "user";

        // 4. Перебираємо кандидатів без рандому — user, user1, user2...
        string candidate = clean;
        int counter = 1;
        while (await _context.Users.AnyAsync(u => u.Username == candidate, token))
        {
            candidate = $"{clean}{counter}";
            counter++;
        }

        return candidate;
    }
    //----------------------------------------
    //CHange password
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

    //Override without checking OldPAssword
    public async Task<ServiceResult<bool>> ChangePasswordAsync(User user, string NewPassword, CancellationToken token = default)
    {
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        await _context.SaveChangesAsync(token);

        return ServiceResult<bool>.Ok(true);
    }
   
    
    //--------------------------------
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
            // Исправлено: используем локальные BodyContent и Subject, а также email переданного юзера
            await _emailService.sendEmailAsync(BodyContent: BodyContent, Subject: Subject, ToAddress: user.Email);
        }
        catch (Exception ex)
        {
            return ServiceResult<string>.Fail($"Failed to send reset email: {ex.Message}");
        }

        return ServiceResult<string>.Ok(number.ToString());
    }

    //SESSIONS
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
    
    public async Task<List<string>> GetActiveSessionsAsync(string email, CancellationToken ctoken = default)
    {
        string devicesKey = $"user_devices:{email}";
        string? json = await _cache.GetStringAsync(devicesKey, ctoken);
        if (string.IsNullOrEmpty(json)) return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(json) ?? new();
    }
   //----------------------------
  
   
   
   public async Task<ServiceResult<string>> ValidateRefreshTokenAsync(string email, string deviceId, string clientToken)
    {
        string cacheKey = $"refresh:{email}:{deviceId}";
        string? savedToken = await _cache.GetStringAsync(cacheKey);
        if (string.IsNullOrEmpty(savedToken) || savedToken != clientToken)
            return ServiceResult<string>.Fail("Invalid or expired refresh token.");
            
        return ServiceResult<string>.Ok(savedToken);
    }
    
  
   
   
   //Save & Validate reset code and verified token for reset password
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
    
    
    
    //Find by smth
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
    
}