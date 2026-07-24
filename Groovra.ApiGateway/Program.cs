using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

// Дефолтный лимит Kestrel (~28.6 MB) меньше, чем лимит аплоада треков на Music-сервисе
// (220 MB) — без этого большие файлы будут падать на уровне шлюза, не долетая до сервиса.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 220_000_000; // 220 MB
});

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("IpBasedLimiter", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(remoteIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("{\"error\": \"Too many requests. Please try again later.\"}", token);
    };
});

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
                    transformContext.ProxyRequest.Headers.Add("X-User-Name", encodedName);
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
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                             ?? new[] { "http://localhost:5178" }; // дефолт, если ничего не передали

        // Vercel даёт каждому preview-деплою уникальный URL вида
        // groovra-frontend-<hash>-berserklegends-projects.vercel.app — хэш меняется
        // при каждом деплое (не только "9wtp"), поэтому ловим его regex'ом, чтобы не
        // редактировать AllowedOrigins после каждого деплоя.
        var vercelPreviewPattern = new Regex(
            @"^https://groovra-frontend-[a-z0-9]+-berserklegends-projects\.vercel\.app$",
            RegexOptions.IgnoreCase);

        policy.SetIsOriginAllowed(origin =>
                allowedOrigins.Contains(origin) || vercelPreviewPattern.IsMatch(origin))
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

        // SignalR-з'єднання (ChatHub) апгрейдяться у сирий WebSocket, а браузерний
        // WebSocket API не вміє виставляти заголовок Authorization — клієнт передає
        // токен через query-string (?access_token=...), тому тут його явно підхоплюємо
        // і кладемо туди, де його чекає стандартна JWT-валідація. Діє лише для /chat/hub,
        // щоб не змінювати поведінку решти маршрутів.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chat/hub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

var app = builder.Build();

// === 4. НАСТРОЙКА КОНВЕЙЕРА ЗАПРОСОВ ===
// Шлюз стоит за Caddy (reverse proxy в docker-сети) — без этого RemoteIpAddress
// всегда будет IP-адресом Caddy, и IpBasedLimiter схлопнется в общий лимит на всех.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseRouting();
app.UseCors();
app.UseRateLimiter();

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
        .AddDocument("music", "Music Service", "/docs/music/openapi.json")
        .AddDocument("history", "History Service", "/docs/history/openapi.json")
        .AddDocument("chat", "Chat Service", "/docs/chat/openapi.json");
});

// === 6. ЗАПУСК ШЛЮЗА YARP ===
app.MapReverseProxy();

app.Run();