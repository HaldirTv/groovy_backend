using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Groovra.Messaging.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

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

var mediaPathConfig = builder.Configuration["MediaStorage:BasePath"];
var mediaPath = string.IsNullOrWhiteSpace(mediaPathConfig)
    ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
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

app.MapControllers();
app.MapGrpcService<Groovra.Auth.Microservice.GRPC.UserNameGrpcService>();
app.Run();