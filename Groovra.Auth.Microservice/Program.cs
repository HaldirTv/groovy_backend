using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Microsoft.EntityFrameworkCore;
// 1. ИСПРАВЛЕННЫЙ USING:
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

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