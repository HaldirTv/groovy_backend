using Groovra.Messaging.Extensions;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Grpc;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc;
using Grpc.Net.Client;
using Hangfire; // Добавлено
using MassTransit;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using StackExchange.Redis;

// gRPC client requires this to call the Auth service over plain HTTP/2 (h2c) in Docker — without it every call throws.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// This service also acts as a gRPC server (TrackInfoGrpcServer). Calling ConfigureKestrel at all
// makes Kestrel stop honoring ASPNETCORE_URLS/HTTP_PORTS for the default endpoint — it must be
// declared explicitly here too, or REST traffic on 8080 silently stops being served.
// gRPC needs its own HTTP/2-only port since Kestrel can't multiplex HTTP/1.1 and HTTP/2 on one
// port without TLS (no ALPN) — without TLS it silently falls back to HTTP/1.1 and gRPC calls fail
// with HTTP_1_1_REQUIRED.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
    options.ListenAnyIP(8081, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddControllers(options =>
{
    options.ModelBinderProviders.Insert(0, new Groovra.Music.Microservice.Binders.GuidListModelBinderProvider());
});

builder.Services.AddMessagingBus(builder.Configuration);

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

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        });

        if (document.Paths != null)
        {
            foreach (var path in document.Paths.Values)
            {
                if (path.Operations is null) continue;
                foreach (var operation in path.Operations.Values)
                {
                    if (operation.RequestBody?.Content is null) continue;
                    if (!operation.RequestBody.Content.TryGetValue("multipart/form-data", out var mediaType)) continue;
                    if (mediaType.Schema?.Properties is null) continue;

                    foreach (var propKey in mediaType.Schema.Properties.Keys.ToList())
                    {
                        if (propKey.Equals("File", StringComparison.OrdinalIgnoreCase) ||
                            propKey.Equals("CoverImage", StringComparison.OrdinalIgnoreCase))
                        {
                            mediaType.Schema.Properties[propKey] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "binary"
                            };
                        }
                    }
                }
            }
        }

        return Task.CompletedTask;
    });
});

// Подключение к общей БД GroovraDB, таблицы в схеме [music]
builder.Services.AddDbContext<MusicDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// === НАСТРОЙКА HANGFIRE (ЭТОГО НЕ ХВАТАЛО) ===
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new Hangfire.SqlServer.SqlServerStorageOptions
        {
            SchemaName = "dbo",                  // Принудительно используем стандартную схему dbo
            PrepareSchemaIfNecessary = false     // Запрещаем Hangfire пытаться создавать/проверять таблицы (мы их уже создали вручную)
        }));

builder.Services.AddHangfireServer();

builder.Services.AddHttpClient();

// Register services (scoped per-request)
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<MusicService>();
builder.Services.AddScoped<FavoritesService>();
builder.Services.AddScoped<PlaylistService>();
builder.Services.AddScoped<AlbumService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<GarbageCollectorService>(); // Добавлено
builder.Services.AddScoped<DownloadService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<CommentsService>();
builder.Services.AddScoped<LyricsService>();
builder.Services.AddScoped<GeminiAiService>();
builder.Services.AddScoped<CacheWarmupService>();

builder.Services.AddHttpClient("lrclib", client =>
{
    client.BaseAddress = new Uri("https://lrclib.net");
    client.DefaultRequestHeaders.Add("Lrclib-Client", "Groovra-Music-Service/1.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddGrpcClient<UserNameGrpcService.UserNameGrpcServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["AuthGrpcUrl"] ?? "https://localhost:7008"); 
    
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    handler.ServerCertificateCustomValidationCallback = 
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});
builder.Services.AddGrpc();

// === РЕГИСТРАЦИЯ REDIS ===
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton<ICacheService, RedisCacheService>();

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Повторные попытки применения миграций на старте — при docker-compose поднятии SQL Server
// может ещё не принимать соединения в момент первого старта сервиса.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        db.Database.Migrate();
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (Music), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

// ── Настройка и раздача static файлов ──────────────────────────────────────────
var basePathConfig = builder.Configuration["MediaStorage:BasePath"];
string mediaPath = string.IsNullOrWhiteSpace(basePathConfig)
    ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
    : Path.GetFullPath(basePathConfig);

if (!Directory.Exists(mediaPath))
{
    Directory.CreateDirectory(mediaPath);
}

// PhysicalFileProvider бросает DirectoryNotFoundException, если папки ещё нет —
// на чистой установке (пустой MediaStorage) сервис из-за этого не стартовал.
var coversPath = Path.Combine(mediaPath, "covers");
if (!Directory.Exists(coversPath))
    Directory.CreateDirectory(coversPath);

var albumCoversPath = Path.Combine(mediaPath, "albumcovers");
if (!Directory.Exists(albumCoversPath))
    Directory.CreateDirectory(albumCoversPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(coversPath),
    RequestPath  = "/music/files/covers"
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(albumCoversPath),
    RequestPath = "/music/files/albumcovers"
});

// ───────────────────────────────────────────────────────────────────────────────

app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<TrackInfoGrpcServer>();

// Пайплайн Hangfire
if (app.Environment.IsDevelopment()) 
{
    app.UseHangfireDashboard(); 
}

// Регистрация джоба
using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    
    recurringJobManager.AddOrUpdate<GarbageCollectorService>(
        "groovra-music-garbage-cleanup",
        service => service.CleanUpGarbageAsync(CancellationToken.None),
        Cron.Daily(3));

    // Прогрев тяжёлых выборок (рекомендации по настрою, топ треков) раз в 12 минут —
    // держит соответствующие API-эндпоинты на чтении из Redis без обращения к SQL.
    recurringJobManager.AddOrUpdate<CacheWarmupService>(
        "groovra-music-cache-warmup",
        service => service.WarmUpAsync(CancellationToken.None),
        "*/12 * * * *");

    // Разовый прогрев сразу после старта, чтобы кеш не пустовал до первого тика cron'а.
    BackgroundJob.Enqueue<CacheWarmupService>(service => service.WarmUpAsync(CancellationToken.None));
}

app.Run();