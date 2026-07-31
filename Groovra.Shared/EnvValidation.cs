using Microsoft.Extensions.Configuration;

namespace Groovra.Shared;

/// <summary>Fail-fast перевірка обов'язкових значень конфігурації одразу при старті сервісу.
/// Без цього відсутній секрет (порожній env var у docker-compose) виявляється лише глибоко
/// в обробнику запиту - незрозумілим NullReferenceException/ArgumentNullException замість
/// чіткого повідомлення в логах контейнера одразу після старту.</summary>
public static class EnvValidation
{
    public static void RequireConfig(IConfiguration configuration, params string[] keys)
    {
        var missing = keys.Where(k => string.IsNullOrWhiteSpace(configuration[k])).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Відсутні обов'язкові налаштування: {string.Join(", ", missing)}. " +
                "Перевірте відповідні змінні оточення в docker-compose.yml / .env на сервері.");
        }
    }
}
