using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

Groovra.Shared.EnvValidation.RequireConfig(builder.Configuration, "Jwt:Key");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("IpBasedLimiter", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 220_000_000; 
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(builderContext =>
    {
        // YARP за замовчуванням підміняє заголовок Host на адресу кластера призначення
        // (напр. "music-service:8080"). Music/Auth-контролери будують публічні URL
        // (audioUrl, посилання на медіа) саме з Request.Host - тому без цього фронтенд
        // отримував би внутрішню docker-адресу замість реального домену, і програвання
        // треків просто не працювало б. AddOriginalHost - офіційний YARP-метод саме для
        // цього (ручне встановлення ProxyRequest.Headers.Host у трансформі ненадійне,
        // forwarder може перезаписати його пізніше в конвеєрі).
        builderContext.AddOriginalHost(true);

        // З'єднання gateway -> music-service всередині мережі docker завжди звичайний HTTP
        // (ASPNETCORE_URLS=http://+:8080), тому Request.Scheme там був би "http", навіть
        // якщо зовнішній клієнт прийшов по HTTPS через Caddy - і Music будувала б audioUrl
        // на http://, що браузер блокує як mixed content з HTTPS-фронтенду. Прокидаємо
        // справжню схему через X-Forwarded-Proto; Music довіряє їй через UseForwardedHeaders.
        builderContext.AddXForwardedProto();

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

var allowedOriginsConfig = builder.Configuration["Cors:AllowedOrigins"] 
    ?? builder.Configuration["CORS_ALLOWED_ORIGINS"] ?? "";
var allowedOriginsList = allowedOriginsConfig
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(o => o.TrimEnd('/'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;
                var trimmedOrigin = origin.TrimEnd('/');
                if (allowedOriginsList.Contains(trimmedOrigin)) return true;

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
                return uri.Host == "localhost" ||
                       uri.Host == "127.0.0.1" ||
                       uri.Host.EndsWith("vercel.app", StringComparison.OrdinalIgnoreCase) ||
                       uri.Host.EndsWith("azurewebsites.net", StringComparison.OrdinalIgnoreCase);
            })
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

var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;

// Ревокація сесій (Session Management "Revoke"/"Revoke all") сама по собі лише прибирає
// refresh-токен з кешу — вже виданий access-token лишається валідним ще до 60 хв (свого
// натурального expiry), тому інший пристрій продовжував працювати, поки сам не оновить
// сторінку (що тригерить /auth/refresh). Тут додаємо живу перевірку: якщо в кеші більше
// немає refresh:{email}:{deviceId} (сесію відкликано або розлогінено), наступний-таки
// запит з цим access-token'ом одразу відхиляється — без очікування на новий refresh.
// Той самий Redis-інстанс і InstanceName, що й у Auth-сервісі (ReglogService) — щоб бачити
// ті самі ключі. Вмикається лише якщо реально налаштований Redis (не "in-memory"
// фолбек), інакше довелося б відхиляти взагалі всі запити через несумісний локальний кеш.
var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
var sessionRevocationEnabled = !string.IsNullOrWhiteSpace(redisConnectionString)
    && !redisConnectionString.Equals("in-memory", StringComparison.OrdinalIgnoreCase);

if (sessionRevocationEnabled)
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "GroovraAuth_";
    });
}

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
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"].FirstOrDefault()
                                  ?? context.Request.Query["token"].FirstOrDefault();
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/chat/hub") || path.Value?.Contains("/stream") == true))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                if (!sessionRevocationEnabled) return;

                var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value;
                var deviceId = context.Principal?.FindFirst("deviceId")?.Value;
                // Токен, виданий до цього фікса, ще не має claim'а deviceId - пропускаємо
                // без перевірки (він і так природно закінчиться протягом Jwt:AccessTokenMinutes).
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(deviceId))
                    return;

                var cache = context.HttpContext.RequestServices.GetRequiredService<IDistributedCache>();
                try
                {
                    var session = await cache.GetStringAsync($"refresh:{email}:{deviceId}");
                    if (session is null)
                    {
                        context.Fail("Сесію відкликано.");
                    }
                }
                catch
                {
                    // Redis тимчасово недоступний - не кладемо автентифікацію всього сайту
                    // через це, пропускаємо запит як валідний (fail-open).
                }
            }
        };
    });

var app = builder.Build();

app.UseForwardedHeaders();
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapScalarApiReference("/scalar/v1", options =>
{
    options.WithTitle("Groovra API Gateway Docs")
           .WithTheme(ScalarTheme.DeepSpace);
    options.AddDocument("auth", "Auth Service", "/docs/auth/openapi.json", isDefault: true)
        .AddDocument("music", "Music Service", "/docs/music/openapi.json")
        .AddDocument("history", "History Service", "/docs/history/openapi.json")
        .AddDocument("chat", "Chat Service", "/docs/chat/openapi.json");
});

app.MapGet("/health", () => Results.Ok()).AllowAnonymous();

app.MapReverseProxy();

app.Run();