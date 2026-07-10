using Groovra.Messaging.Extensions;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Grpc;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc;
using Grpc.Net.Client;
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
        // Указываем относительный корень "/", чтобы Scalar автоматически
        // подставлял хост Gateway (https://localhost:7005).
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

builder.Services.AddHttpClient();

// Register services (scoped per-request)
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<MusicService>();
builder.Services.AddScoped<FavoritesService>();
builder.Services.AddScoped<PlaylistService>();
builder.Services.AddScoped<AlbumService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddGrpcClient<UserNameGrpcService.UserNameGrpcServiceClient>(o =>
{
    // Возьми этот URL из appsettings.json, либо захардкодь для локальной разработки
    o.Address = new Uri(builder.Configuration["AuthGrpcUrl"] ?? "https://localhost:7008"); 
    
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    // Отключаем паранойю по поводу локальных сертификатов
    handler.ServerCertificateCustomValidationCallback = 
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});
builder.Services.AddGrpc();

// === РЕГИСТРАЦИЯ REDIS ===
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
{
    // ConfigurationOptions автоматически управляет переподключениями, если Redis моргнет
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false; // Не ронять микросервис, если Redis временно недоступен при старте
    
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

// ── Настройка и раздача статических файлов (ПЕРЕНЕСЕНО НАВЕРХ) ──────────────────
// Находим правильный абсолютный путь
var basePathConfig = builder.Configuration["MediaStorage:BasePath"];
string mediaPath = string.IsNullOrWhiteSpace(basePathConfig)
    ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
    : Path.GetFullPath(basePathConfig);

// Сами создаем папку MediaStorage в проекте, если её еще нет, чтобы не было ошибок
if (!Directory.Exists(mediaPath))
{
    Directory.CreateDirectory(mediaPath);
}

// Раздаём ТОЛЬКО обложки (covers/) как статику.
var coversPath = Path.Combine(mediaPath, "covers");
if (!Directory.Exists(coversPath))
    Directory.CreateDirectory(coversPath);

// Статика должна обрабатываться ДО авторизации и контроллеров, чтобы быть общедоступной
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(coversPath),
    RequestPath  = "/music/files/covers"
});
// ───────────────────────────────────────────────────────────────────────────────

app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<TrackInfoGrpcServer>();

app.Run();