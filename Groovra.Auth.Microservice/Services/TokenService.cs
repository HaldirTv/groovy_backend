using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Groovra.Auth.Microservice.Models;
using Microsoft.IdentityModel.Tokens;

namespace Groovra.Auth.Microservice.Services;

public class TokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateToken(User user)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name,user.Username)
        };
        // Добавляем только роли из таблицы Roles
        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Name));
        }

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
        
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Время жизни access-токена берём из конфига (Jwt:AccessTokenMinutes), по умолчанию 60 мин.
        // Раньше было жёстко 5 мин — это заставляло фронт дёргать /auth/refresh каждые ~5 минут и
        // на каждой перезагрузке страницы, а любой сбой такого refresh разлогинивал пользователя.
        var accessTokenMinutes = _configuration.GetValue<int?>("Jwt:AccessTokenMinutes") ?? 60;

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(accessTokenMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}