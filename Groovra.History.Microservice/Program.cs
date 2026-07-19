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
        return Task.CompletedTask;
    });
});
builder.Services.AddMessagingBus(builder.Configuration, typeof(Program).Assembly);

var app = builder.Build();

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