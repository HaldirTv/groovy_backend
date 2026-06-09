using Groovra.Auth.Microservice.Data; 
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Models;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Services;

public class ReglogService
{
    private readonly AuthDbContext _context;
    private readonly TokenService _tokenService;
    private readonly EmailService _emailService;
    public ReglogService(AuthDbContext context, TokenService tokenService,EmailService  emailService)
    {
        _context = context; 
        _tokenService = tokenService;
        _emailService = emailService;
    }

    public async Task<User> RegisterAsync(RegisterDto registerUser,CancellationToken token = default)
    {
        var emailExists = await _context.Users.AnyAsync(u => u.Email == registerUser.Email,token);
        if (emailExists)
        {
            throw new Exception("Email already exists");
        }

        bool isArtist = registerUser.Role.Equals("Artist", StringComparison.OrdinalIgnoreCase);
        List<Role> roles = new();
        if (isArtist){

            Role? artistRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Artist",token);
            if(artistRole !=null) roles.Add(artistRole);
        }
        Role? listenerRole = await _context.Roles.FirstOrDefaultAsync(r=>r.Name == "Listener",token);
        if (listenerRole!=null) roles.Add(listenerRole);
        
        var user = new User
        {
            Username = registerUser.Username,
            Email = registerUser.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(registerUser.Password)
        };

        // Привязываем роль
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
        
        return user;
    }

    public async Task<User?> ValidateUserForLoginAsync(LoginDto loginUser, CancellationToken token = default)
    {
        
        var user = await _context.Users
            .Include(u => u.Roles) 
            .FirstOrDefaultAsync(u => u.Email == loginUser.Email, token);

        
        if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.PasswordHash))
        {
            return null; 
        }
    
        return user; 
    }
    
    /// <summary>
    /// Перевіряє юзерский старий пароль, якщо він вірний, то оновлює його на новий і зберігає зміни в базі даних.
    /// </summary>
    /// <param name="user"></param>
    /// <param name="OldPassword"></param>
    /// <param name="NewPassword"></param>
    /// <param name="token"></param>
    /// <exception cref="Exception"></exception>
    public async Task ChangePasswordAsync(User user,string OldPassword,string NewPassword,CancellationToken token = default)
    {
        
        if (!BCrypt.Net.BCrypt.Verify(OldPassword, user.PasswordHash))
        {
            throw new Exception("Текущий пароль неверный.");
        }
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        await _context.SaveChangesAsync(token);
        
    }
    /// <summary>
    /// Просто скидує пароль без перевірки
    /// </summary>
    /// <param name="user"></param>
    /// <param name="NewPassword"></param>
    /// <param name="token"></param>
    public async Task ChangePasswordAsync(User user,string NewPassword,CancellationToken token = default)
    {
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);
        await _context.SaveChangesAsync(token);
    }
    

    public async Task<string> RequestPasswordResetAsync(User user, CancellationToken cancellationToken)
    {
        int number = Random.Shared.Next(100000,999999);
        string BodyContent = $"Ваш код {number} введіть на сайті для підтвердження";
        string Subject = "Сброс пароля в Groovra";
        await _emailService.sendEmailAsync(BodyContent:BodyContent,ToAddress:user.Email,Subject:Subject);
        return number.ToString();
    }

    public async Task<User?> FindUserByIdAsync(Guid userId, bool loadReference = true, CancellationToken token = default)
    {
        IQueryable<User> query = _context.Users;
        
        if (loadReference)
        {
            query = query.Include(u => u.Roles);
        }

        return await query.FirstOrDefaultAsync(u => u.Id == userId, token);
    }

    public async Task<User?> FindUserByEmailAsync(string email, bool loadReference = true, CancellationToken cancellationToken = default)
    {
        IQueryable<User> query = _context.Users;

        if (loadReference)
        {
            query = query.Include(u => u.Roles);
        }

        return await query.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
    }
}