using Groovra.Messaging.Extensions;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Grpc;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc;
using Grpc.Net.Client;
using Hangfire; 
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using StackExchange.Redis;

Groovra.Shared.DotEnvLoader.LoadFromNearestEnvFile();
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnection");
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnectionRemote");
Groovra.Shared.DotEnvLoader.MapIfPresent("GEMINI_API_KEY", "Gemini__ApiKey");
Groovra.Shared.DotEnvLoader.MapIfPresent("OPENMODEL_API_KEY", "OpenModel__ApiKey");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options =>
{
    options.ModelBinderProviders.Insert(0, new Groovra.Music.Microservice.Binders.GuidListModelBinderProvider());
})
.AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddMessagingBus(builder.Configuration, typeof(Program).Assembly);

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

        if (document.Paths != null)
        {
            foreach (var path in document.Paths.Values)
            {
                if (path.Operations != null)
                {
                    foreach (var operation in path.Operations.Values)
                    {
                        if (operation.RequestBody?.Content != null &&
                            operation.RequestBody.Content.TryGetValue("multipart/form-data", out var mediaType))
                        {
                            if (mediaType.Schema?.Properties != null)
                            {
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
                }
            }
        }

        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<MusicDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
           .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();

builder.Services.AddHttpClient();

builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<MusicService>();
builder.Services.AddScoped<FavoritesService>();
builder.Services.AddScoped<PlaylistService>();
builder.Services.AddScoped<AlbumService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<GarbageCollectorService>(); 
builder.Services.AddScoped<DownloadService>();         
builder.Services.AddScoped<LibraryService>();          

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("lrclib", client =>
{
    client.BaseAddress = new Uri("https://lrclib.net");
    client.DefaultRequestHeaders.Add("Lrclib-Client", "Groovra-Music-Service/1.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<LyricsService>();

builder.Services.AddHttpClient("openmodel", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["OpenModel:BaseUrl"] ?? "https://api.openmodel.ai/v1/";
    var apiKey = config["OpenModel:ApiKey"] ?? "";
    client.BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<OpenModelService>();

builder.Services.AddHttpClient("gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<GeminiAiService>();
builder.Services.AddScoped<CommentsService>();
builder.Services.AddScoped<CacheWarmupService>();

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

var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton<ICacheService, RedisCacheService>();

var app = builder.Build();

// Блокирующий вызов с ретраями (как у остальных сервисов) - приложение не начинает
// принимать трафик, пока схема реально не накатана.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Database migration warning (Music): {ex.Message}");
            // Аварийный фолбэк на случай гонки миграций на общей БД (Type - nvarchar(450),
            // чтобы совпадать с уже исправленным маппингом Download.Type в MusicDbContext).
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'[music].[Downloads]'))
                BEGIN
                    CREATE TABLE [music].[Downloads] (
                        [Id] int NOT NULL IDENTITY,
                        [UserId] uniqueidentifier NOT NULL,
                        [Type] nvarchar(450) NOT NULL,
                        [ItemId] uniqueidentifier NULL,
                        [AlbumName] nvarchar(256) NULL,
                        [ArtistName] nvarchar(256) NULL,
                        [DownloadedAt] datetime2 NOT NULL,
                        CONSTRAINT [PK_Downloads] PRIMARY KEY ([Id])
                    );
                END
                IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') IS NOT NULL
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = N'20260720113450_AddDownloads')
                    BEGIN
                        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
                        VALUES (N'20260720113450_AddDownloads', N'10.0.0-preview.1.25080.5');
                    END
                END
            ");
            db.Database.Migrate();
        }
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (Music), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var lyricsService = scope.ServiceProvider.GetRequiredService<LyricsService>();
    await lyricsService.InitializeAsync();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// AppContext.BaseDirectory, а не текущий каталог процесса: статика /music/files/* должна
// раздаваться из той же папки, куда пишет UploadService, независимо от способа запуска.
var basePathConfig = builder.Configuration["MediaStorage:BasePath"];
string mediaPath = string.IsNullOrWhiteSpace(basePathConfig)
    ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
    : Path.GetFullPath(basePathConfig);

try
{
    if (!Directory.Exists(mediaPath)) Directory.CreateDirectory(mediaPath);
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: Could not create mediaPath {mediaPath}: {ex.Message}");
    mediaPath = Path.Combine(Path.GetTempPath(), "MediaStorage");
    try { Directory.CreateDirectory(mediaPath); } catch { }
}

var coversPath = Path.Combine(mediaPath, "covers");
try { if (!Directory.Exists(coversPath)) Directory.CreateDirectory(coversPath); } catch { }

var albumCoversPath = Path.Combine(mediaPath, "albumcovers");
try { if (!Directory.Exists(albumCoversPath)) Directory.CreateDirectory(albumCoversPath); } catch { }

try
{
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
}
catch { }

app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<TrackInfoGrpcServer>();

if (app.Environment.IsDevelopment()) 
{
    app.UseHangfireDashboard(); 
}

using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobManager.AddOrUpdate<GarbageCollectorService>(
        "groovra-music-garbage-cleanup",
        service => service.CleanUpGarbageAsync(CancellationToken.None),
        Cron.Daily(3));

    recurringJobManager.AddOrUpdate<CacheWarmupService>(
        "groovra-music-cache-warmup",
        service => service.WarmUpAsync(CancellationToken.None),
        "*/12 * * * *");

    BackgroundJob.Enqueue<CacheWarmupService>(service => service.WarmUpAsync(CancellationToken.None));
}

app.Run();