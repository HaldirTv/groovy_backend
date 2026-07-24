using Amazon.Runtime;
using Amazon.S3;
using Groovra.ChatService.Microservice.Data;
using Groovra.ChatService.Microservice.Hubs;
using Groovra.ChatService.Microservice.Services;
using Groovra.Shared.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

// gRPC client requires this to call the Music/Auth services over plain HTTP/2 (h2c) in Docker — without it every call throws.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// 1. Контроллеры, SignalR та OpenApi документація
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

builder.Services.AddAuthorization();

// Завантаження медіа в чат (картинки/голосові/файли) — Cloudflare R2, S3-сумісний API.
builder.Services.Configure<CloudflareR2Options>(builder.Configuration.GetSection("CloudflareR2"));
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudflareR2Options>>().Value;
    var s3Config = new AmazonS3Config
    {
        ServiceURL = options.ServiceUrl,
        ForcePathStyle = true,
        // R2 не підтримує AWS-специфічну (SigV4 streaming/trailer) checksum-обробку, яку
        // AWSSDK.S3 4.x вмикає за замовчуванням для PutObject — без цього PutObjectAsync
        // падає з помилкою на боці R2.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };
    var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
    return new AmazonS3Client(credentials, s3Config);
});
builder.Services.AddHttpClient<IFileStorageService, FileStorageService>();

// 2. Подключение к ОБЩЕЙ БД MS SQL Server
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlServer(connectionString));

// gRPC-клієнт до Music — гідратація даних треку при "поділитись треком у чаті"
// (той самий контракт і паттерн реєстрації, що вже використовує History).
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

// gRPC-клієнт до Auth — резолвить ім'я запрошеного учасника при створенні бесіди
// (той самий контракт і паттерн реєстрації, що вже використовує Music).
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

// 3. Трансформер документації Scalar (один в один як в Auth/Music/History)
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

var app = builder.Build();

// Повторні спроби застосування міграцій на старті — при docker-compose підйомі SQL Server
// може ще не приймати з'єднання в момент першого старту сервісу.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.Database.Migrate();
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (Chat), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

// 4. Маппінг Scalar для інтерфейсу
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/chat/hub");

app.Run();
