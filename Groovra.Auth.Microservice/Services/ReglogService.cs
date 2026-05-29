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

        var user = new User
        {
            Username = registerUser.Username,
            Email = registerUser.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(registerUser.Password),
            Role = registerUser.Role.Equals("Artist", StringComparison.OrdinalIgnoreCase) ? "Artist" : "Listener"
        };

        if (user.Role.Equals("Artist", StringComparison.OrdinalIgnoreCase))//!!!!!!!!
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
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == loginUser.Email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(loginUser.Password, user.PasswordHash))
        {
            return null; // Невірний email або пароль
        }
        return _tokenService.GenerateToken(user);
    }
}