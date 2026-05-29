using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddOpenApi();

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
var mediaPath = builder.Configuration["MediaStorage:BasePath"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage");

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(mediaPath),
    RequestPath = "/music/files"
});

app.Run();
