var builder = WebApplication.CreateBuilder(args);

// === БЛОК 1: РЕГИСТРАЦИЯ СЕРВИСОВ (Всегда ДО builder.Build) ===

// 1. Регистрируем YARP и говорим ему читать настройки из appsettings.json
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// 2. Регистрируем CORS (перенесли сюда, чтобы .NET не ругался)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5178") // Наш фронт
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Собираем приложение
var app = builder.Build();

// === БЛОК 2: НАСТРОЙКА КОНВЕЙЕРА (Порядок имеет значение!) ===

// 3. СНАЧАЛА проверяем и одобряем CORS-запрос от браузера Никиты
app.UseCors();

// 4. И ТОЛЬКО ПОТОМ, если CORS одобрен, YARP перенаправляет запрос на микросервисы
app.MapReverseProxy();

app.Run();