#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using TagFile = TagLib.File;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    /// <summary>
    /// Отвечает за формирование и наполнение плейлиста:
    /// чтение тегов, добавление файлов/папок/CUE.
    /// </summary>
    public class PlaylistService
    {
        static PlaylistService()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Latin1Encoding = Encoding.GetEncoding("iso-8859-1");
            Cp1251Encoding = Encoding.GetEncoding(1251);
        }

        private static readonly Encoding Latin1Encoding;
        private static readonly Encoding Cp1251Encoding;

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
            int safeIdx = Math.Max(0, index);
            string title = IOPath.GetFileNameWithoutExtension(path);
            string artist = "Неизвестен";
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
                {
                    var performers = tag.Tag.Performers
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(FixEncoding)
                        .ToArray();
                    if (performers.Length > 0)
                        artist = string.Join(", ", performers);
                }

                var ts = tag.Properties.Duration;
                if (ts > TimeSpan.Zero)
                    duration = FormatTime(ts);

                cover = TryGetOrLoadCoverFromTag(tag, coverKey);
            }
            catch { }

            var item = new TrackItem
            {
                Path = path,
                Title = title,
                Artist = artist,
                Duration = duration,
                Format = IOPath.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                Index = safeIdx + 1,
                Color1 = Grad1[safeIdx % Grad1.Length],
                Color2 = Grad2[safeIdx % Grad2.Length],
                CoverCacheKey = coverKey,
            };

            if (cover != null)
                item.CoverImage = cover;

            return item;
        }

        /// <summary>Строит список TrackItem из CUE-файла.</summary>
        public List<TrackItem> BuildCueItems(string cuePath, int startIndex)
        {
            var result = new List<TrackItem>();
            if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
                return result;

            var cueTracks = CueParser.Parse(cuePath);
            if (cueTracks == null || cueTracks.Count == 0)
                return result;

            int idx = Math.Max(0, startIndex);

            string? firstAudioWithFile = cueTracks
                .Select(ct => ct.AudioFile)
                .FirstOrDefault(f => !string.IsNullOrEmpty(f) && File.Exists(f));

            string? sharedCoverKey = !string.IsNullOrEmpty(firstAudioWithFile)
                ? (IOPath.GetDirectoryName(firstAudioWithFile) ?? firstAudioWithFile)
                : null;

            BitmapSource? sharedCover = null;
            TimeSpan totalAudioDur = TimeSpan.Zero;

            bool needCover = sharedCoverKey != null && !TrackItem.CoverCache.ContainsKey(sharedCoverKey);
            bool needTotalDuration = cueTracks.Any(ct => ct.End <= ct.Start);

            if (!string.IsNullOrEmpty(firstAudioWithFile) && (needCover || needTotalDuration))
            {
                try
                {
                    using var tag = TagFile.Create(firstAudioWithFile);
                    totalAudioDur = tag.Properties.Duration;
                    if (sharedCoverKey != null)
                        sharedCover = TryGetOrLoadCoverFromTag(tag, sharedCoverKey);
                }
                catch { }
            }
            else if (sharedCoverKey != null)
            {
                TrackItem.CoverCache.TryGetValue(sharedCoverKey, out sharedCover);
            }

            foreach (var ct in cueTracks)
            {
                if (string.IsNullOrWhiteSpace(ct.AudioFile) || !File.Exists(ct.AudioFile))
                    continue;

                TimeSpan durTs = ct.End > ct.Start
                    ? ct.End - ct.Start
                    : (totalAudioDur > ct.Start ? totalAudioDur - ct.Start : TimeSpan.Zero);
                string dur = durTs > TimeSpan.Zero ? FormatTime(durTs) : "—";

                var item = new TrackItem
                {
                    Path = ct.AudioFile,
                    Title = string.IsNullOrWhiteSpace(ct.Title) ? $"Track {ct.Number}" : FixEncoding(ct.Title),
                    Artist = string.IsNullOrWhiteSpace(ct.Performer) ? "Неизвестен" : FixEncoding(ct.Performer),
                    Duration = dur,
                    Format = IOPath.GetExtension(ct.AudioFile).TrimStart('.').ToUpperInvariant() + " CUE",
                    Index = idx + 1,
                    Color1 = Grad1[idx % Grad1.Length],
                    Color2 = Grad2[idx % Grad2.Length],
                    IsCue = true,
                    CueStart = ct.Start,
                    CueEnd = ct.End,
                    CoverCacheKey = sharedCoverKey,
                };

                if (sharedCover != null)
                    item.CoverImage = sharedCover;

                result.Add(item);
                idx++;
            }

            return result;
        }

        /// <summary>
        /// Асинхронно сканирует папку, возвращает все треки (CUE имеют приоритет).
        /// </summary>
        public async Task<(List<TrackItem> Tracks, HashSet<string> CoveredFiles)> ScanFolderAsync(
            string folderPath, HashSet<string>? existingPaths, int startIndex)
        {
            var tracks = new List<TrackItem>();
            var coveredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return (tracks, coveredFiles);

            existingPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int safeStart = Math.Max(0, startIndex);

            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            return await Task.Run(() =>
            {
                var cueFiles = Directory.EnumerateFiles(folderPath, "*.cue", enumOpts)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var cf in cueFiles)
                {
                    try
                    {
                        var items = BuildCueItems(cf, safeStart + tracks.Count);
                        foreach (var item in items)
                        {
                            coveredFiles.Add(item.Path);
                            if (!existingPaths.Contains(item.Path))
                                tracks.Add(item);
                        }
                    }
                    catch { }
                }

                var files = Directory.EnumerateFiles(folderPath, "*.*", enumOpts)
                    .Where(f => SupportedExtSet.Contains(IOPath.GetExtension(f))
                             && !coveredFiles.Contains(f)
                             && !existingPaths.Contains(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var f in files)
                    tracks.Add(BuildTrackItem(f, safeStart + tracks.Count));

                return (tracks, coveredFiles);
            });
        }

        // ─── Вспомогательные ─────────────────────────────────────────────────────

        /// <summary>Исправляет теги ID3, сохранённые как CP1251 в Latin-1 обёртке.</summary>
        public static string FixEncoding(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Быстрая проверка: если все символы ASCII или уже есть символы > 0xFF (например, UTF-8 кириллица) — не перекодируем
            bool hasHighLatin1 = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c > 0xFF) return input;
                if (c >= 0x80) hasHighLatin1 = true;
            }
            if (!hasHighLatin1) return input;

            try
            {
                byte[] bytes = Latin1Encoding.GetBytes(input);
                string decoded = Cp1251Encoding.GetString(bytes);
                for (int i = 0; i < decoded.Length; i++)
                {
                    char c = decoded[i];
                    if ((c >= 'А' && c <= 'я') || c == 'ё' || c == 'Ё')
                        return decoded;
                }
            }
            catch { }

            return input;
        }

        public static string FormatTime(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        private static BitmapSource? TryGetOrLoadCoverFromTag(TagFile tag, string coverKey)
        {
            if (TrackItem.CoverCache.TryGetValue(coverKey, out var cached))
                return cached;

            try
            {
                var pic = tag.Tag.Pictures?.FirstOrDefault();
                if (pic?.Data?.Data is not { Length: > 0 } bytes)
                    return null;

                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = 68; // достаточно для 34px @2x
                bmp.EndInit();
                bmp.Freeze();

                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}

#nullable restore