using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.Services;
using Microsoft.EntityFrameworkCore;
// 1. ИСПРАВЛЕННЫЙ USING:
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddGrpc();
builder.Services.AddOpenApi();


builder.Services.AddAuthorization(); 

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ReglogService>();
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
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();


app.UseAuthorization(); 

app.MapControllers();
app.MapGrpcService<Groovra.Auth.Microservice.GRPC.UserNameGrpcService>();
app.Run();