using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// === 1. РЕГИСТРАЦИЯ YARP ===
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

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

builder.Services.AddAuthorization();

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