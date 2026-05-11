using System;
using System.IO;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    /// <summary>Сохранение и загрузка настроек приложения в JSON.</summary>
    public static class SettingsService
    {
        private static readonly string SettingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AuroraPlayer", "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented  = true,
            // double.NaN/Infinity не являются валидным JSON — разрешаем их чтение
            // (System.Text.Json по умолчанию кидает исключение на NaN при десериализации)
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch { }
        }

        public static AppSettings? Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            }
            catch { return null; }
        }
    }
}
