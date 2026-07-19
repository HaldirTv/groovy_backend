using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
// 1. ИСПРАВЛЕННЫЙ USING:
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// This service also acts as a gRPC server (UserNameGrpcService). Without TLS, Kestrel can't
// negotiate HTTP/1.1 vs HTTP/2 on one endpoint (no ALPN) — it silently falls back to HTTP/1.1
// and rejects h2c with HTTP_1_1_REQUIRED. So gRPC gets its own HTTP/2-only port; REST (port 8080
// from ASPNETCORE_URLS) stays HTTP/1.1 as before.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8081, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddControllers();
builder.Services.AddGrpc();
builder.Services.AddOpenApi();


builder.Services.AddAuthorization(); 

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ReglogService>();
builder.Services.AddTransient<EmailService>();

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

builder.Services.AddStackExchangeRedisCache(options =>
{

    options.Configuration = builder.Configuration["Redis:ConnectionString"];
    
    // Префікс GroovraAuth_ автоматично дописуєтся до ключів цього сервісу, щоб не було конфліктів імен бо redis збереження для всіх сервісів 
    options.InstanceName = "GroovraAuth_"; 
});
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

var mediaPathConfig = builder.Configuration["MediaStorage:BasePath"];
var mediaPath = string.IsNullOrWhiteSpace(mediaPathConfig)
    ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
    : Path.GetFullPath(mediaPathConfig);

Directory.CreateDirectory(mediaPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(mediaPath),
    RequestPath = "/media"
});

app.UseHttpsRedirection();


app.UseAuthorization(); 

app.MapControllers();
app.MapGrpcService<Groovra.Auth.Microservice.GRPC.UserNameGrpcService>();
app.Run();