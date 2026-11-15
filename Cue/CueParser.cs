#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public class CueTrack
    {
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string Performer { get; set; } = "";
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; } = TimeSpan.Zero; // Zero = до конца файла
        public string AudioFile { get; set; } = "";
    }

    public static class CueParser
    {
        private static readonly char[] WhitespaceChars = { ' ', '\t' };

        static CueParser()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static List<CueTrack> Parse(string cuePath)
        {
            var tracks = new List<CueTrack>();
            if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
                return tracks;

            var lines = ReadCueLines(cuePath);
            string dir = IOPath.GetDirectoryName(cuePath) ?? "";
            string audioFile = "";
            string albumPerformer = "";
            CueTrack? current = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (StartsWithKeyword(line, "FILE"))
                {
                    int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                    string rawName;
                    if (q1 >= 0 && q2 > q1)
                    {
                        rawName = line.Substring(q1 + 1, q2 - q1 - 1);
                    }
                    else
                    {
                        string rest = line.Substring(4).Trim();
                        int space = rest.LastIndexOfAny(WhitespaceChars);
                        rawName = space > 0 ? rest.Substring(0, space).Trim() : rest;
                    }

                    audioFile = ResolveCueAudioFile(dir, rawName);
                    if (current != null && string.IsNullOrEmpty(current.AudioFile))
                        current.AudioFile = audioFile;
                }
                else if (StartsWithKeyword(line, "PERFORMER"))
                {
                    string val = ExtractQuotedOrRaw(line, 9);
                    if (current == null) albumPerformer = val;
                    else current.Performer = val;
                }
                else if (StartsWithKeyword(line, "TITLE"))
                {
                    string val = ExtractQuotedOrRaw(line, 5);
                    if (current != null) current.Title = val;
                }
                else if (StartsWithKeyword(line, "TRACK"))
                {
                    var parts = line.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int num))
                    {
                        // Пропускаем неаудио-дорожки (например, TRACK 01 MODE1/2352)
                        if (parts.Length >= 3 &&
                            parts[2].StartsWith("MODE", StringComparison.OrdinalIgnoreCase))
                        {
                            current = null;
                            continue;
                        }

                        current = new CueTrack { Number = num, AudioFile = audioFile };
                        tracks.Add(current);
                    }
                }
                else if (StartsWithKeyword(line, "INDEX") && current != null)
                {
                    var idxParts = line.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries);
                    if (idxParts.Length >= 3 && int.TryParse(idxParts[1], out int indexNum))
                    {
                        if (indexNum == 1)
                        {
                            // INDEX 01 — точка начала трека (приоритет)
                            current.Start = ParseCueTime(idxParts[2]);
                        }
                        else if (indexNum == 0 && current.Start == TimeSpan.Zero)
                        {
                            // INDEX 00 — pregap, используем только если INDEX 01 отсутствует
                            current.Start = ParseCueTime(idxParts[2]);
                        }
                    }
                }
            }

            foreach (var t in tracks)
                if (string.IsNullOrEmpty(t.Performer))
                    t.Performer = albumPerformer;

            for (int i = 0; i < tracks.Count - 1; i++)
            {
                if (string.Equals(tracks[i].AudioFile, tracks[i + 1].AudioFile, StringComparison.OrdinalIgnoreCase) &&
                    tracks[i + 1].Start > tracks[i].Start)
                {
                    tracks[i].End = tracks[i + 1].Start;
                }
            }

            return tracks;
        }

        // ─── Приватные helpers ────────────────────────────────────────────────────

        private static bool StartsWithKeyword(string line, string keyword)
        {
            return line.Length > keyword.Length &&
                   line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
                   char.IsWhiteSpace(line[keyword.Length]);
        }

        private static string ResolveCueAudioFile(string dir, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "";

            string candidate = IOPath.Combine(dir, rawName);
            if (File.Exists(candidate))
                return candidate;

            // Частый случай: в CUE прописан .wav, а файл рядом сжат в .flac/.ape/.wv
            string stem = IOPath.GetFileNameWithoutExtension(rawName);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                foreach (string ext in PlaylistService.SupportedExt)
                {
                    string alt = IOPath.Combine(dir, stem + ext);
                    if (File.Exists(alt))
                        return alt;
                }
            }

            return candidate;
        }

        /// <summary>
        /// Читает CUE с автоопределением кодировки: UTF-8 BOM → UTF-16 LE/BE → UTF-8 → CP1251 → Latin-1.
        /// </summary>
        private static string[] ReadCueLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string[] sep = { "\r\n", "\n", "\r" };

            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split(sep, StringSplitOptions.None);

            // UTF-16 LE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Split(sep, StringSplitOptions.None);

            // UTF-16 BE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Split(sep, StringSplitOptions.None);

            // UTF-8 без BOM
            try
            {
                string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                return text.Split(sep, StringSplitOptions.None);
            }
            catch { }

            // CP1251
            try
            {
                return Encoding.GetEncoding(1251).GetString(bytes).Split(sep, StringSplitOptions.None);
            }
            catch { }

            // Latin-1 fallback
            return Encoding.Latin1.GetString(bytes).Split(sep, StringSplitOptions.None);
        }

        private static string ExtractQuotedOrRaw(string line, int keywordLength)
        {
            string rest = line.Substring(keywordLength).Trim();
            if (rest.Length > 0 && rest[0] == '"')
            {
                int q2 = rest.IndexOf('"', 1);
                return q2 > 0 ? rest.Substring(1, q2 - 1) : rest.Trim('"');
            }
            return rest;
        }

        private static TimeSpan ParseCueTime(string s)
        {
            // MM:SS:FF  (FF = frames, 75 per second)
            var parts = s.Split(':');
            if (parts.Length < 3) return TimeSpan.Zero;

            int.TryParse(parts[0], out int mm);
            int.TryParse(parts[1], out int ss);
            int.TryParse(parts[2], out int ff);

            if (mm < 0) mm = 0;
            if (ss < 0) ss = 0;
            if (ff < 0) ff = 0;

            return TimeSpan.FromSeconds(mm * 60.0 + ss + ff / 75.0);
        }
    }
}

#nullable restore