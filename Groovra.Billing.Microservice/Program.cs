using Groovra.Billing.Microservice.Data;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

Groovra.Shared.DotEnvLoader.LoadFromNearestEnvFile();
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_SECRET_KEY", "Stripe__SecretKey");
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_PUBLISHABLE_KEY", "Stripe__PublishableKey");
Groovra.Shared.DotEnvLoader.MapIfPresent("STRIPE_WEBHOOK_SECRET", "Stripe__WebhookSecret");

var builder = WebApplication.CreateBuilder(args);

// Stripe-ключі навмисно НЕ перевіряються тут при старті: БД в цьому сервісі - локальний
// SQLite-файл (нижче), не залежить від жодної env-змінної, тому вимагати нічого не треба.
// Сам Stripe-функціонал (PaymentController) уже толерантний до відсутніх ключів через "?? \"\""
// і впаде зрозумілою Stripe-помилкою лише в момент реального виклику оплати - так і було
// задумано, це не регресія, яку треба "фіксити" блокуванням старту сервісу.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddOpenApi();

// Окрема піддиректорія (не сам AppContext.BaseDirectory) - щоб можна було змонтувати
// одним docker volume саме дані, а не всю папку застосунку. Без volume взагалі кожен
// docker compose up --build стирав би всю історію підписок/платежів разом з контейнером.
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "billing.db");
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

app.MapGet("/health", () => Results.Ok());

app.MapControllers();
app.MapOpenApi();

app.MapScalarApiReference("/scalar/v1", options =>
{
    options.WithTitle("Groovra Billing Microservice Docs")
           .WithTheme(ScalarTheme.DeepSpace);
});

app.Run();
