using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
// 1. ИСПРАВЛЕННЫЙ USING:
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// This service also acts as a gRPC server (UserNameGrpcService). Calling ConfigureKestrel at all
// makes Kestrel stop honoring ASPNETCORE_URLS/HTTP_PORTS for the default endpoint — it must be
// declared explicitly here too, or REST traffic on 8080 silently stops being served.
// gRPC needs its own HTTP/2-only port since Kestrel can't multiplex HTTP/1.1 and HTTP/2 on one
// port without TLS (no ALPN) — without TLS it silently falls back to HTTP/1.1 and gRPC calls fail
// with HTTP_1_1_REQUIRED.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
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

// Strategy pattern: which IEmailSender implementation is active is switched via
// "Email:Provider" config ("Mailtrap" for the sandbox, "Smtp" for a generic mailbox,
// "Brevo" for the production SMTP relay). Options завжди біндяться незалежно від того,
// який провайдер зараз активний — дешево, і дозволяє переключити Provider без зміни
// реєстрацій нижче.
builder.Services.Configure<MailtrapOptions>(builder.Configuration.GetSection("Email:Mailtrap"));
builder.Services.Configure<BrevoOptions>(builder.Configuration.GetSection("Email:Brevo"));

var emailProvider = builder.Configuration["Email:Provider"];
if (string.Equals(emailProvider, "Smtp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IEmailSender, SmtpEmailService>();
}
else if (string.Equals(emailProvider, "Brevo", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IEmailSender, BrevoSmtpEmailService>();
}
else
{
    builder.Services.AddTransient<IEmailSender, MailtrapEmailService>();
}

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

builder.Services.AddStackExchangeRedisCache(options =>
{

    options.Configuration = builder.Configuration["Redis:ConnectionString"];
    
    // Префікс GroovraAuth_ автоматично дописуєтся до ключів цього сервісу, щоб не було конфліктів імен бо redis збереження для всіх сервісів 
    options.InstanceName = "GroovraAuth_"; 
});
var app = builder.Build();

// Повторные попытки применения миграций на старте — при docker-compose поднятии SQL Server
// может ещё не принимать соединения в момент первого старта сервиса.
for (int i = 0; i < 15; i++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.Database.Migrate();
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration failed (Auth), retrying ({i + 1}/15)... Error: {ex.Message}");
        Thread.Sleep(3000);
    }
}

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