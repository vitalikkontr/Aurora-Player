#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    /// <summary>Сохранение и загрузка настроек приложения в JSON.</summary>
    public static class SettingsService
    {
        private static readonly object _sync = new();

        private static readonly string SettingsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AuroraPlayer", "settings.json");

        private static readonly string BackupPath = SettingsPath + ".bak";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // Сохраняем совместимость с уже существующими settings.json, где могли записаться NaN/Infinity
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static void Save(AppSettings? settings)
        {
            if (settings == null) return;

            string? tmpPath = null;
            try
            {
                lock (_sync)
                {
                    string? dir = IOPath.GetDirectoryName(SettingsPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    tmpPath = $"{SettingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

                    File.WriteAllText(tmpPath, json, Encoding.UTF8);

                    if (File.Exists(SettingsPath))
                    {
                        try
                        {
                            File.Replace(tmpPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
                        }
                        catch
                        {
                            File.Move(tmpPath, SettingsPath, overwrite: true);
                        }
                    }
                    else
                    {
                        File.Move(tmpPath, SettingsPath, overwrite: true);
                    }

                    tmpPath = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SettingsService.Save error: {ex}");
                FfmpegService.AppendDecodeLog($"SETTINGS_SAVE_ERR {ex.Message}");
            }
            finally
            {
                if (tmpPath != null)
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
        }

        public static AppSettings? Load()
        {
            lock (_sync)
            {
                return TryLoadFrom(SettingsPath) ?? TryLoadFrom(BackupPath);
            }
        }

        private static AppSettings? TryLoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return null;

                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SettingsService.Load ({path}) error: {ex}");
                FfmpegService.AppendDecodeLog($"SETTINGS_LOAD_ERR file='{path}' err='{ex.Message}'");
                return null;
            }
        }
    }
}

#nullable restore