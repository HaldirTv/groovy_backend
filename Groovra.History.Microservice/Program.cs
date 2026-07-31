using Groovra.History.Microservice.Consumers;
using Groovra.History.Microservice.Controllers;
using Groovra.History.Microservice.Data;
using Groovra.Messaging.Extensions;
using Groovra.Shared.Grpc;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

Groovra.Shared.DotEnvLoader.LoadFromNearestEnvFile();
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnection");
Groovra.Shared.DotEnvLoader.MapIfPresent("DB_CONNECTION_STRING", "ConnectionStrings__DefaultConnectionRemote");

var builder = WebApplication.CreateBuilder(args);

Groovra.Shared.EnvValidation.RequireConfig(builder.Configuration,
    "ConnectionStrings:DefaultConnection");

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddOpenApi();

builder.Services.AddAuthorization(); 

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<HistoryDbContext>(options =>
    options.UseSqlServer(connectionString)
           .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

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

        var securityRequirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        };

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(securityRequirement);

        return Task.CompletedTask;
    });
});
builder.Services.AddMessagingBus(builder.Configuration, typeof(Program).Assembly, "history");

var app = builder.Build();

for (int i = 0; i < 15; i++)
{
    try
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HistoryDbContext>();
            db.Database.Migrate();
            break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (History), retrying ({i + 1}/15)... Error: {ex.Message}");
        System.Threading.Thread.Sleep(3000);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok());

app.MapControllers();
app.Run();