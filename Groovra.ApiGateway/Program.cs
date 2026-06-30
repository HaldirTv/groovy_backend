using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);


// === 1. РЕГИСТРАЦИЯ YARP ===
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(builderContext =>
    {
        builderContext.AddRequestTransform(async transformContext =>
        {
            var user = transformContext.HttpContext.User;

            
            transformContext.ProxyRequest.Headers.Remove("X-User-Id");
            transformContext.ProxyRequest.Headers.Remove("X-User-Name");
            transformContext.ProxyRequest.Headers.Remove("X-User-Role");

           
            if (user.Identity?.IsAuthenticated == true)
            {
                var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub");
                var userNameClaim = user.FindFirst(ClaimTypes.Name) ?? user.FindFirst("name");

                var encodedName = WebUtility.UrlEncode(userNameClaim?.Value ?? "");
                if (userIdClaim != null && !string.IsNullOrEmpty(userIdClaim.Value))
                {
                    
                    var roleClaims = user.Claims
                        .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                        .Select(c => c.Value)
                        .ToList();

                    if (!roleClaims.Any()) 
                        roleClaims.Add("Listener");

                    string rolesValue = string.Join(",", roleClaims);

          
                    transformContext.ProxyRequest.Headers.Add("X-User-Id", userIdClaim.Value);
                    transformContext.ProxyRequest.Headers.Add("X-User-Name", userNameClaim?.Value ?? "");
                    transformContext.ProxyRequest.Headers.Add("X-User-Role", rolesValue);
                }
            }
            await Task.CompletedTask;
        });
    });

// === 2. РЕГИСТРАЦИЯ CORS ===
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5178", "https://localhost:7005") // Добавили адрес Gateway!
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddAuthorization(options =>
{

    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, "Admin"));

    options.AddPolicy("ArtistOnly", policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, "Artist","Admin"));
    

});

// === 3. РЕГИСТРАЦИЯ JWT АВТОРИЗАЦИИ ===
var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

var app = builder.Build();

// === 4. НАСТРОЙКА КОНВЕЙЕРА ЗАПРОСОВ ===

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// === 5. ПОДКЛЮЧЕНИЕ SCALAR UI ===
app.MapScalarApiReference("/scalar/v1", options =>
{
    options.WithTitle("Groovra API Gateway Docs")
           .WithTheme(ScalarTheme.DeepSpace);
    
    // Мы говорим Scalar-у тянуть JSON по этому адресу,
    // а YARP перехватит этот запрос и отправит его в Auth-микросервис.
    options.AddDocument("auth", "Auth Service", "/docs/auth/openapi.json", isDefault: true)
        .AddDocument("music","Music Service","docs/music/openapi.json");
});

// === 6. ЗАПУСК ШЛЮЗА YARP ===
app.MapReverseProxy();

app.Run();