using Groovra.Messaging.Extensions;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Grpc;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc;
using Grpc.Net.Client;
using Hangfire; // Добавлено
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

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
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

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

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

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

var coversPath = Path.Combine(mediaPath, "covers");
if (!Directory.Exists(coversPath))
    Directory.CreateDirectory(coversPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(coversPath),
    RequestPath  = "/music/files/covers"
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(mediaPath, "albumcovers")),
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
}

app.Run();