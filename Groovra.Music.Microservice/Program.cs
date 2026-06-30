using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
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


// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Serve stored media files as static content (for local dev).
// In production, replace with a CDN or dedicated file-serving middleware.
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
// Аудиофайлы (audio/) намеренно НЕ включены: они отдаются через
// streaming endpoint /music/tracks/{id}/stream с поддержкой HTTP Range.
var coversPath = Path.Combine(mediaPath, "covers");
if (!Directory.Exists(coversPath))
    Directory.CreateDirectory(coversPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(coversPath),
    RequestPath  = "/music/files/covers"
});


app.Run();