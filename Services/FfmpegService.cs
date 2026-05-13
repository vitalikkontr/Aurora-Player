using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    /// <summary>
    /// Всё что связано с поиском ffmpeg, декодированием APE/WV и работой с путями.
    /// </summary>
    public static class FfmpegService
    {
        public const int  ProbeRetryCount      = 60;  // 60 × 50ms = 3s (было 900 × 50ms = 45s)
        public const int  ProbeRetryDelayMs    = 50;
        // APE через именованный pipe делает seek в конец файла при инициализации —
        // это невозможно через однонаправленный pipe. Поэтому APE/WV с кириллическим
        // путём сразу открываем через ApeRingBufferStream минуя FfmpegDecodeStream.

        private static readonly object _apeCacheSync = new();
        private static readonly Dictionary<string, string> _apeNormalizedCache =
            new(StringComparer.OrdinalIgnoreCase);

        // ─── Поиск ffmpeg ─────────────────────────────────────────────────────────

        public static string? FindFfmpeg()
        {
            string? exeDir = IOPath.GetDirectoryName(Environment.ProcessPath ?? "");

            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                string p = IOPath.Combine(exeDir ?? "", name);
                if (File.Exists(p)) return p;
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
                foreach (var dir in pathEnv.Split(';'))
                {
                    string p = IOPath.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }

            string pf        = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx       = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localApp  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProf  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var searchDirs = new[]
            {
                IOPath.Combine(pf,  "ffmpeg", "bin"),
                IOPath.Combine(pfx, "ffmpeg", "bin"),
                IOPath.Combine(@"C:\ffmpeg", "bin"),
                IOPath.Combine(@"C:\ffmpeg"),
                IOPath.Combine(@"C:\tools\ffmpeg", "bin"),
                IOPath.Combine(localApp, "Microsoft", "WinGet", "Packages",
                    "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe", "ffmpeg-7.1-full_build", "bin"),
                IOPath.Combine(localApp, "Microsoft", "WinGet", "Packages",
                    "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe", "ffmpeg-7.0-full_build", "bin"),
                IOPath.Combine(userProf, "scoop", "apps", "ffmpeg", "current", "bin"),
                IOPath.Combine(userProf, "scoop", "shims"),
                IOPath.Combine(@"C:\ProgramData\chocolatey", "bin"),
                IOPath.Combine(pf, "Chocolatey", "bin"),
            };

            foreach (var dir in searchDirs)
            {
                string p = IOPath.Combine(dir, "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }

            // WinGet dynamic search (ограничиваем глубину до 5 уровней чтобы не тормозить)
            try
            {
                string wingetPkgs = IOPath.Combine(localApp, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetPkgs))
                {
                    var found = Directory.EnumerateFiles(wingetPkgs, "ffmpeg.exe",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            MaxRecursionDepth     = 5,
                            IgnoreInaccessible    = true,
                        }).FirstOrDefault();
                    if (found != null) return found;
                }
            }
            catch { }

            return null;
        }

        // ─── Пути ────────────────────────────────────────────────────────────────

        public static bool PathHasNonAscii(string path) => path.Any(c => c > 127);

        /// <summary>
        /// Возвращает гарантированно ASCII-путь к временной папке.
        /// GetTempPath() на русскоязычных Windows может вернуть путь с кириллицей.
        /// </summary>
        public static string GetSafeAsciiTempPath()
        {
            string temp = IOPath.GetTempPath();
            if (!PathHasNonAscii(temp)) return temp;

            try { string ctemp = @"C:\Temp"; Directory.CreateDirectory(ctemp); return ctemp; }
            catch { }

            string? exeDir = IOPath.GetDirectoryName(Environment.ProcessPath ?? "");
            if (exeDir != null && !PathHasNonAscii(exeDir)) return exeDir;

            return temp;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufSize);

        /// <summary>8.3 short path — часто нужен ffmpeg на путях с кириллицей.</summary>
        public static string? TryGetWindowsShortPath(string longPath)
        {
            try
            {
                if (string.IsNullOrEmpty(longPath) || !File.Exists(longPath)) return null;
                var sb = new StringBuilder(4096);
                int n  = GetShortPathName(longPath, sb, sb.Capacity);
                if (n <= 0 || n >= sb.Capacity) return null;
                string s = sb.ToString();
                if (string.Equals(s, longPath, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(s) ? s : null;
            }
            catch { return null; }
        }

        // ─── APE нормализация ─────────────────────────────────────────────────────

        public static bool TryFindApeMacHeaderOffset(string path, out int macOffset)
        {
            macOffset = 0;
            try
            {
                if (!File.Exists(path) || !path.EndsWith(".ape", StringComparison.OrdinalIgnoreCase))
                    return false;

                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length < 4) return false;

                int    scanBytes = (int)Math.Min(input.Length, 16L * 1024 * 1024);
                var    scan      = new byte[scanBytes];
                int    read      = input.Read(scan, 0, scanBytes);

                for (int i = 0; i <= read - 4; i++)
                    if (scan[i] == 'M' && scan[i+1] == 'A' && scan[i+2] == 'C' && scan[i+3] == ' ')
                    { macOffset = i; return true; }

                return false;
            }
            catch { return false; }
        }

        public static bool TryCreateApeWithoutLeadingId3(string originalPath, out string normalizedApePath)
        {
            normalizedApePath = "";
            try
            {
                if (!File.Exists(originalPath) ||
                    !originalPath.EndsWith(".ape", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!TryFindApeMacHeaderOffset(originalPath, out int macOffset) || macOffset <= 0)
                    return false;

                lock (_apeCacheSync)
                {
                    if (_apeNormalizedCache.TryGetValue(originalPath, out string? cached) && File.Exists(cached))
                    { normalizedApePath = cached; return true; }
                    _apeNormalizedCache.Remove(originalPath);
                }

                using var input = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string tmp = IOPath.Combine(GetSafeAsciiTempPath(), $"aurora_ape_norm_{Guid.NewGuid():N}.ape");
                input.Position = macOffset;
                using (var output = new FileStream(tmp, FileMode.Create))
                    input.CopyTo(output);

                if (!File.Exists(tmp) || new FileInfo(tmp).Length < 128)
                { try { File.Delete(tmp); } catch { } return false; }

                lock (_apeCacheSync)
                {
                    // Лимит кэша: не более 10 записей, выбрасываем самую старую
                    if (_apeNormalizedCache.Count >= 10)
                    {
                        string oldest = _apeNormalizedCache.Keys.First();
                        if (_apeNormalizedCache.TryGetValue(oldest, out string? oldPath))
                            try { File.Delete(oldPath); } catch { }
                        _apeNormalizedCache.Remove(oldest);
                    }
                    _apeNormalizedCache[originalPath] = tmp;
                }
                normalizedApePath = tmp;
                return true;
            }
            catch { return false; }
        }

        public static void CleanupApeCache()
        {
            lock (_apeCacheSync)
            {
                foreach (string path in _apeNormalizedCache.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                _apeNormalizedCache.Clear();
            }
        }

        /// <summary>
        /// Удаляет все временные файлы aurora_* оставшиеся от прошлых сессий (после крэшей).
        /// Вызывать один раз при старте приложения.
        /// </summary>
        public static void CleanupStaleTempFiles()
        {
            try
            {
                string tempDir = GetSafeAsciiTempPath();
                foreach (string file in Directory.GetFiles(tempDir, "aurora_*"))
                {
                    try
                    {
                        // Пропускаем файлы моложе 60 секунд — они могут быть нашими текущими
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds < 60) continue;
                        File.Delete(file);
                    }
                    catch { }
                }
                // C:\Temp тоже чистим если использовался как fallback
                if (tempDir != @"C:\Temp" && Directory.Exists(@"C:\Temp"))
                {
                    foreach (string file in Directory.GetFiles(@"C:\Temp", "aurora_*"))
                    {
                        try
                        {
                            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds < 60) continue;
                            File.Delete(file);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static List<(string Path, bool DeleteAfter)> BuildInputPathVariants(
            string originalPath, bool includeNormalizedApe = true)
        {
            var  list = new List<(string, bool)>();
            var  seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string p, bool del) { if (File.Exists(p) && seen.Add(p)) list.Add((p, del)); }

            Add(originalPath, false);
            if (TryGetWindowsShortPath(originalPath) is string shortP) Add(shortP, false);
            if (includeNormalizedApe && TryCreateApeWithoutLeadingId3(originalPath, out string norm))
                Add(norm, false);
            // Копирование файла в temp удалено — FfmpegDecodeStream передаёт
            // кириллические пути через именованный Windows pipe без копирования.
            return list;
        }

        // ─── Logging ─────────────────────────────────────────────────────────────

        private static readonly string DecodeLogPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AuroraPlayer", "decode.log");

        /// <summary>Установить в true чтобы включить запись decode.log для отладки.</summary>
        public static bool LoggingEnabled = false;

        public static void AppendDecodeLog(string message)
        {
            if (!LoggingEnabled) return;
            try
            {
                string? dir = IOPath.GetDirectoryName(DecodeLogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(DecodeLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
