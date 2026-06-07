using Groovra.Auth.Microservice.Data; 
using Groovra.Auth.Microservice.DTOs;
using Groovra.Auth.Microservice.Models;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Services;

public class ReglogService
{
    private readonly AuthDbContext _context;
    private readonly TokenService _tokenService;
   
    public ReglogService(AuthDbContext context, TokenService tokenService)
    {
        _context = context; 
        _tokenService = tokenService;
    }

    public async Task<User> RegisterAsync(RegisterDto registerUser)
    {
        var emailExists = await _context.Users.AnyAsync(u => u.Email == registerUser.Email);
        if (emailExists)
        {
            throw new Exception("Email already exists");
        }

        bool isArtist = registerUser.Role.Equals("Artist", StringComparison.OrdinalIgnoreCase);
        List<Role> roles = new();
        if (isArtist){

            Role? artistRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == "Artist");
            if(artistRole !=null) roles.Add(artistRole);
        }
        Role? listenerRole = await _context.Roles.FirstOrDefaultAsync(r=>r.Name == "Listener");
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
        await _context.SaveChangesAsync();
        
        return user;
    }

    public async Task<string?> LoginAsync(LoginDto loginUser)
    {
        // Подтягиваем роли при авторизации
        var user = await _context.Users
            .Include(u => u.Roles) 
            .FirstOrDefaultAsync(u => u.Email == loginUser.Email);

        if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.PasswordHash))
        {
            return null;
        }
        
        return _tokenService.GenerateToken(user);
    }
}