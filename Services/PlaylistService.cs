using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using TagFile = TagLib.File;
using IOPath  = System.IO.Path;

namespace AuroraPlayer
{
    /// <summary>
    /// Отвечает за формирование и наполнение плейлиста:
    /// чтение тегов, добавление файлов/папок/CUE.
    /// </summary>
    public class PlaylistService
    {
        // Поддерживаемые расширения
        public static readonly string[] SupportedExt =
            { ".mp3", ".flac", ".wav", ".aiff", ".aif", ".ogg", ".opus",
              ".m4a", ".aac", ".wma", ".ape", ".mp4", ".m4b", ".wv" };

        public static readonly HashSet<string> SupportedExtSet =
            new(SupportedExt, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Grad1 =
            { "#1A1A3E","#2D1120","#0D2233","#2A1800","#0D2010","#1E1A00" };
        private static readonly string[] Grad2 =
            { "#2D1B4E","#4E1A35","#1A3D5C","#4A3000","#1A3D20","#3C3000" };

        // ─── Публичные методы ─────────────────────────────────────────────────────

        /// <summary>Создаёт один TrackItem из пути к файлу, читая теги.</summary>
        public TrackItem BuildTrackItem(string path, int index)
        {
            string title    = IOPath.GetFileNameWithoutExtension(path);
            string artist   = "Неизвестен";
            string duration = "—";
            BitmapSource? cover = null;

            // Ключ кэша обложек — папка файла (треки одного альбома делят одну BitmapSource)
            string coverKey = IOPath.GetDirectoryName(path) ?? path;

            try
            {
                using var tag = TagFile.Create(path);
                if (!string.IsNullOrWhiteSpace(tag.Tag.Title))
                    title = FixEncoding(tag.Tag.Title);
                if (tag.Tag.Performers?.Length > 0)
                    artist = string.Join(", ", tag.Tag.Performers.Select(FixEncoding));
                var ts = tag.Properties.Duration;
                duration = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";

                cover = TryGetOrLoadCover(path, coverKey);
            }
            catch { }

            var item = new TrackItem
            {
                Path           = path,
                Title          = title,
                Artist         = artist,
                Duration       = duration,
                Format         = IOPath.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                Index          = index + 1,
                Color1         = Grad1[index % Grad1.Length],
                Color2         = Grad2[index % Grad2.Length],
                CoverCacheKey  = coverKey,
            };
            // Записываем в кэш через свойство (или напрямую если кэша нет)
            if (cover != null) item.CoverImage = cover;
            return item;
        }

        /// <summary>Строит список TrackItem из CUE-файла.</summary>
        public List<TrackItem> BuildCueItems(string cuePath, int startIndex)
        {
            var result    = new List<TrackItem>();
            var cueTracks = CueParser.Parse(cuePath);
            int idx       = startIndex;

            // Общая обложка для всего CUE-альбома — берём из первого аудиофайла
            string? sharedCoverKey = null;
            string? firstAudioWithFile = cueTracks
                .Select(ct => ct.AudioFile)
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(firstAudioWithFile))
                sharedCoverKey = IOPath.GetDirectoryName(firstAudioWithFile) ?? firstAudioWithFile;

            BitmapSource? sharedCover = null;
            if (sharedCoverKey != null && firstAudioWithFile != null)
                sharedCover = TryGetOrLoadCover(firstAudioWithFile, sharedCoverKey);

            foreach (var ct in cueTracks)
            {
                if (!File.Exists(ct.AudioFile)) continue;
                string dur = ct.End > ct.Start ? FormatTime(ct.End - ct.Start) : "—";
                var item = new TrackItem
                {
                    Path          = ct.AudioFile,
                    Title         = string.IsNullOrEmpty(ct.Title) ? $"Track {ct.Number}" : ct.Title,
                    Artist        = ct.Performer,
                    Duration      = dur,
                    Format        = IOPath.GetExtension(ct.AudioFile).TrimStart('.').ToUpperInvariant() + " CUE",
                    Index         = idx + 1,
                    Color1        = Grad1[idx % Grad1.Length],
                    Color2        = Grad2[idx % Grad2.Length],
                    IsCue         = true,
                    CueStart      = ct.Start,
                    CueEnd        = ct.End,
                    CoverCacheKey = sharedCoverKey,
                };
                if (sharedCover != null) item.CoverImage = sharedCover;
                result.Add(item);
                idx++;
            }
            return result;
        }

        /// <summary>
        /// Асинхронно сканирует папку, возвращает все треки (CUE имеют приоритет).
        /// </summary>
        public async Task<(List<TrackItem> Tracks, HashSet<string> CoveredFiles)> ScanFolderAsync(
            string folderPath, HashSet<string> existingPaths, int startIndex)
        {
            var tracks       = new List<TrackItem>();
            var coveredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // CUE-файлы имеют приоритет
            string[] cueFiles = await Task.Run(() =>
                Directory.GetFiles(folderPath, "*.cue", SearchOption.AllDirectories)
                    .OrderBy(f => f).ToArray());

            foreach (var cf in cueFiles)
            {
                try
                {
                    var items = BuildCueItems(cf, startIndex + tracks.Count);
                    foreach (var item in items)
                    {
                        coveredFiles.Add(item.Path);
                        tracks.Add(item);
                    }
                }
                catch { }
            }

            // Обычные аудио-файлы, не покрытые CUE
            string[] files = await Task.Run(() =>
                Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtSet.Contains(IOPath.GetExtension(f))
                             && !coveredFiles.Contains(f)
                             && !existingPaths.Contains(f))
                    .OrderBy(f => f).ToArray());

            var batch = await Task.Run(() =>
            {
                var items = new List<TrackItem>();
                foreach (var f in files)
                    items.Add(BuildTrackItem(f, startIndex + tracks.Count + items.Count));
                return items;
            });

            tracks.AddRange(batch);
            return (tracks, coveredFiles);
        }

        // ─── Вспомогательные ─────────────────────────────────────────────────────

        /// <summary>Исправляет теги ID3, сохранённые как CP1251 в Latin-1 обёртке.</summary>
        public static string FixEncoding(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            try
            {
                var latin1  = Encoding.GetEncoding("iso-8859-1");
                var cp1251  = Encoding.GetEncoding(1251);
                var bytes   = latin1.GetBytes(input);
                if (bytes.All(b => b < 0x80)) return input;
                var decoded = cp1251.GetString(bytes);
                bool hasCyrillic = decoded.Any(c => (c >= 'А' && c <= 'я') || c == 'ё' || c == 'Ё');
                if (hasCyrillic) return decoded;
            }
            catch { }
            return input;
        }

        public static string FormatTime(TimeSpan ts)
            => $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";

        private static BitmapSource? TryGetOrLoadCover(string audioPath, string coverKey)
        {
            if (TrackItem.CoverCache.TryGetValue(coverKey, out var cached))
                return cached;

            try
            {
                using var tag = TagFile.Create(audioPath);
                var pic = tag.Tag.Pictures?.FirstOrDefault();
                if (pic?.Data?.Data == null || pic.Data.Data.Length == 0) return null;

                using var ms = new MemoryStream(pic.Data.Data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                bmp.StreamSource     = ms;
                bmp.DecodePixelWidth = 68; // достаточно для 34px @2x
                bmp.EndInit();
                bmp.Freeze();

                TrackItem.CoverCache[coverKey] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
