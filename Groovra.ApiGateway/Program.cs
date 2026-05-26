var builder = WebApplication.CreateBuilder(args);

// 1. Регистрируем YARP и говорим ему читать настройки из секции "ReverseProxy" в appsettings.json
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// 2. Включаем проксирование запросов
app.MapReverseProxy();

app.Run();