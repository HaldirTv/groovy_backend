using Groovra.Billing.Microservice.Data;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

Groovra.Shared.DotEnvLoader.LoadFromNearestEnvFile();
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_SECRET_KEY", "Stripe__SecretKey");
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_PUBLISHABLE_KEY", "Stripe__PublishableKey");
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_WEBHOOK_SECRET", "Stripe__WebhookSecret");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddOpenApi();

var dbPath = Path.Combine(AppContext.BaseDirectory, "billing.db");
builder.Services.AddDbContext<BillingDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.MapControllers();
app.MapOpenApi();

app.MapScalarApiReference("/scalar/v1", options =>
{
    options.WithTitle("Groovra Billing Microservice Docs")
           .WithTheme(ScalarTheme.DeepSpace);
});

app.Run();
