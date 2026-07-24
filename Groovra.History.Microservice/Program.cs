using Groovra.History.Microservice.Consumers;
using Groovra.History.Microservice.Controllers;
using Groovra.History.Microservice.Data;
using Groovra.Messaging.Extensions;
using Groovra.Shared.Grpc;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

// gRPC client requires this to call the Music service over plain HTTP/2 (h2c) in Docker — without it every call throws.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// 1. Контроллеры и OpenApi документация
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddAuthorization(); 

// 2. Подключение к ОБЩЕЙ БД MS SQL Server
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<HistoryDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddGrpcClient<TrackInfoGrpcService.TrackInfoGrpcServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["MusicGrpcUrl"] ?? "https://localhost:7176");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    handler.ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
});

// 3. Трансформер документации Scalar (один в один как в Auth)
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

        return Task.CompletedTask;
    });
});
builder.Services.AddMessagingBus(builder.Configuration, typeof(Program).Assembly);

var app = builder.Build();

// Повторные попытки применения миграций на старте — при docker-compose поднятии SQL Server
// может ещё не принимать соединения в момент первого старта сервиса.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HistoryDbContext>();
        db.Database.Migrate();
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (History), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

// 4. Маппинг Scalar для интерфейса
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthorization(); 

app.MapControllers();
app.Run();