using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
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

// Register services (scoped per-request)
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<MusicService>();

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

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(mediaPath),
    RequestPath = "/music/files"
});

app.Run();
