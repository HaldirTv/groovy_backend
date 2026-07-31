using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Groovra.Messaging.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.Extensions.FileProviders;

// Локально (dotnet run, без docker-compose) секрети беремо з кореневого .env замість того,
// щоб тримати їх у appsettings.Development.json. У контейнері docker-compose вже прокидає
// ASP.NET-змінні напряму - MapIfPresent там нічого не перезапише.
Groovra.Shared.DotEnvLoader.LoadFromNearestEnvFile();
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnection");
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnectionRemote");
Groovra.Shared.DotEnvLoader.MapIfPresent("JWT_SECRET_KEY", "Jwt__Key");
Groovra.Shared.DotEnvLoader.MapIfPresent("MAIL_USER", "Email__Mailtrap__Username");
Groovra.Shared.DotEnvLoader.MapIfPresent("MAIL_PASS", "Email__Mailtrap__Password");
Groovra.Shared.DotEnvLoader.MapIfPresent("BREVO_SMTP_USERNAME", "Email__Brevo__Username");
Groovra.Shared.DotEnvLoader.MapIfPresent("BREVO_SMTP_PASSWORD", "Email__Brevo__Password");
Groovra.Shared.DotEnvLoader.MapIfPresent("BREVO_FROM_EMAIL", "Email__Brevo__FromEmail");
Groovra.Shared.DotEnvLoader.MapIfPresent("GOOGLE_CLIENT_ID", "Authentication__Google__ClientId");
Groovra.Shared.DotEnvLoader.MapIfPresent("GOOGLE_CLIENT_SECRET", "Authentication__Google__ClientSecret");

var builder = WebApplication.CreateBuilder(args);

Groovra.Shared.EnvValidation.RequireConfig(builder.Configuration,
    "ConnectionStrings:DefaultConnection",
    "Jwt:Key");

// Без TLS Kestrel не може сам розрізняти HTTP/1.1 і HTTP/2 на одному порту (потрібне ALPN).
// Цей сервіс приймає gRPC (UserNameGrpcService) від Chat/Music по внутрішній docker-мережі -
// без розділення портів кожен такий виклик падав з "HTTP_1_1_REQUIRED". 8080 лишається
// чистим REST для gateway (EXPOSE 8081 у Dockerfile уже був, просто не використовувався).
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(8081, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

builder.Services.AddMessagingBus(builder.Configuration);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddGrpc();
builder.Services.AddOpenApi();

builder.Services.AddAuthorization(); 

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ReglogService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<AdminService>();

// AdminService тягне лічильник AI-треків з Music-сервісу (GET /music/stats/ai-tracks-count)
// для дашборда. Адреса приходить з конфіга/оточення - у compose це http://music-service:8080.
builder.Services.AddHttpClient("MusicService", client =>
{
    var musicServiceUrl = builder.Configuration["Services:MusicServiceUrl"] ?? "http://localhost:5002";
    client.BaseAddress = new Uri(musicServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.Configure<MailtrapOptions>(builder.Configuration.GetSection("Email:Mailtrap"));
builder.Services.Configure<BrevoOptions>(builder.Configuration.GetSection("Email:Brevo"));

var emailProvider = builder.Configuration["Email:Provider"];
if (string.Equals(emailProvider, "Smtp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IEmailSender, SmtpEmailService>();
}
else if (string.Equals(emailProvider, "Brevo", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IEmailSender, BrevoSmtpEmailService>();
}
else
{
    builder.Services.AddTransient<IEmailSender, MailtrapEmailService>();
}

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Servers = new List<OpenApiServer>
        {
            new OpenApiServer { Url = "/" } 
        };

        var securityScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter your JWT token (without 'Bearer ' prefix)"
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = securityScheme;

        var securityRequirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        };

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(securityRequirement);

        return Task.CompletedTask;
    });
});

var redisConn = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConn) && !redisConn.Equals("in-memory", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConn;
            options.InstanceName = "GroovraAuth_"; 
        });
    }
    catch
    {
        builder.Services.AddDistributedMemoryCache();
    }
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
var app = builder.Build();

// Блокирующий вызов (как у остальных сервисов) - приложение не начинает принимать
// трафик, пока схема реально не накатана через настоящие EF-миграции. Раньше здесь стоял
// fire-and-forget Task.Run, который вообще не вызывал Database.Migrate() - только ручные
// ALTER TABLE ADD COLUMN патчи поверх УЖЕ существующих таблиц, то есть на чистой БД ни
// одна таблица никогда бы не создалась.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.Database.Migrate();
        // Точечные патчи для колонок/таблиц, добавленных вручную мимо формальных миграций
        // на общей БД (2FA, follow, settings) - идемпотентны, безопасны при повторном вызове.
        Groovra.Auth.Microservice.MigrateDbHelper.EnsureColumns(db);
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (Auth), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// AppContext.BaseDirectory, а не текущий каталог процесса: аватары/баннеры должны
// раздаваться из той же папки, куда их пишет ProfileController, независимо от запуска.
var mediaPathConfig = builder.Configuration["MediaStorage:BasePath"];
var mediaPath = string.IsNullOrWhiteSpace(mediaPathConfig)
    ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
    : Path.GetFullPath(mediaPathConfig);

try
{
    Directory.CreateDirectory(mediaPath);
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: Could not create mediaPath {mediaPath}: {ex.Message}");
    mediaPath = Path.Combine(Path.GetTempPath(), "MediaStorage");
    try { Directory.CreateDirectory(mediaPath); } catch { }
}

try
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(mediaPath),
        RequestPath = "/media"
    });
}
catch { }

app.UseForwardedHeaders();
app.UseAuthorization(); 

// Стає доступним лише ПІСЛЯ блокуючого циклу міграцій вище (app.Run() - остання команда) -
// тому docker-compose healthcheck на цей ендпоінт коректно відрізняє "процес стартував" від
// "реально готовий приймати трафік" (без цього залежні сервіси могли ловити 502/connection
// refused у перші секунди після старту).
app.MapGet("/health", () => Results.Ok());

app.MapControllers();
app.MapGrpcService<Groovra.Auth.Microservice.GRPC.UserNameGrpcService>();
app.Run();