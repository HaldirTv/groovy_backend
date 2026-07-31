namespace Groovra.Shared;

/// <summary>Підвантажує секрети з кореневого .env у змінні оточення процесу перед
/// WebApplication.CreateBuilder — щоб appsettings.Development.json міг лишатися порожнім
/// (як і "продовий" appsettings.json), не тримаючи реальні паролі/ключі в git. У docker-compose
/// ці ж змінні вже приходять напряму від оточення контейнера, .env там не потрібен - тому
/// існуючі значення оточення завжди мають пріоритет і ніколи не перезаписуються.</summary>
public static class DotEnvLoader
{
    public static void LoadFromNearestEnvFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                Load(candidate);
                return;
            }
            dir = dir.Parent;
        }
    }

    private static void Load(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>Копіює значення .env-змінної (довільна назва, як у .env) у змінну оточення з
    /// ASP.NET-ієрархічною назвою (Section__Key), яку реально читає IConfiguration - той самий
    /// маппінг, що вже прописаний у docker-compose.yml для деплою. Нічого не робить, якщо
    /// цільова змінна вже встановлена (наприклад, реальним оточенням контейнера) або джерело
    /// в .env відсутнє.</summary>
    public static void MapIfPresent(string envVarName, string aspNetCoreKey)
    {
        if (Environment.GetEnvironmentVariable(aspNetCoreKey) is not null) return;
        var value = Environment.GetEnvironmentVariable(envVarName);
        if (value is not null)
            Environment.SetEnvironmentVariable(aspNetCoreKey, value);
    }
}
