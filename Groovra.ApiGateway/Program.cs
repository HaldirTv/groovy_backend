using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
 
    options.AddPolicy("IpBasedLimiter", context =>
    {
       
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(remoteIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100, // Сколько запросов разрешено
            Window = TimeSpan.FromMinutes(1), // За какой промежуток времени
            QueueLimit = 0 // Очередь для заблокированных запросов (0 — сразу отсекать)
        });
    });

    // Что возвращать, если лимит превышен (обычно 429 Too Many Requests)
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

        policy.WithOrigins(allowedOrigins)
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
        .AddDocument("music", "Music Service", "docs/music/openapi.json")
        .AddDocument("history", "History Service", "/docs/history/openapi.json");
});

// === 6. ЗАПУСК ШЛЮЗА YARP ===
app.MapReverseProxy();

app.Run();