using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public class CueTrack
    {
        public int      Number    { get; set; }
        public string   Title     { get; set; } = "";
        public string   Performer { get; set; } = "";
        public TimeSpan Start     { get; set; }
        public TimeSpan End       { get; set; } = TimeSpan.Zero; // Zero = до конца файла
        public string   AudioFile { get; set; } = "";
    }

    public static class CueParser
    {
        public static List<CueTrack> Parse(string cuePath)
        {
            var    tracks         = new List<CueTrack>();
            var    lines          = ReadCueLines(cuePath);
            string dir            = IOPath.GetDirectoryName(cuePath) ?? "";
            string audioFile      = "";
            string albumPerformer = "";
            CueTrack? current     = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                    if (q1 >= 0 && q2 > q1)
                    {
                        // С кавычками: FILE "name with spaces.wav" WAVE
                        audioFile = IOPath.Combine(dir, line.Substring(q1 + 1, q2 - q1 - 1));
                    }
                    else
                    {
                        // Без кавычек: FILE name.wav WAVE — убираем первое слово (FILE)
                        // и последнее слово (тип: WAVE/MP3/AIFF/...), берём всё что между
                        string rest  = line.Substring(5).Trim(); // убираем "FILE "
                        int    space = rest.LastIndexOf(' ');
                        string name  = space > 0 ? rest.Substring(0, space).Trim() : rest;
                        audioFile    = IOPath.Combine(dir, name);
                    }
                }
                else if (line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                {
                    string val = ExtractQuotedOrRaw(line, "PERFORMER ");
                    if (current == null) albumPerformer = val;
                    else current.Performer = val;
                }
                else if (line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                {
                    string val = ExtractQuotedOrRaw(line, "TITLE ");
                    if (current != null) current.Title = val;
                }
                else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int num))
                    {
                        current = new CueTrack { Number = num, AudioFile = audioFile };
                        tracks.Add(current);
                    }
                }
                else if (line.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase) && current != null)
                {
                    var idxParts = line.Split(' ');
                    if (idxParts.Length >= 2)
                    {
                        bool is01 = idxParts[1] == "01";
                        bool is00 = idxParts[1] == "00";
                        if (is01)
                        {
                            // INDEX 01 — точка начала трека (приоритет)
                            current.Start = ParseCueTime(line.Substring(8).Trim());
                        }
                        else if (is00 && current.Start == TimeSpan.Zero)
                        {
                            // INDEX 00 — pregap, используем только если INDEX 01 отсутствует
                            current.Start = ParseCueTime(line.Substring(8).Trim());
                        }
                    }
                }
            }

            // Заполняем Performer из альбома если не задан у трека
            foreach (var t in tracks)
                if (string.IsNullOrEmpty(t.Performer)) t.Performer = albumPerformer;

            // Заполняем End = Start следующего трека
            for (int i = 0; i < tracks.Count - 1; i++)
                if (tracks[i].AudioFile == tracks[i + 1].AudioFile)
                    tracks[i].End = tracks[i + 1].Start;

            return tracks;
        }

        // ─── Приватные helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Читает CUE с автоопределением кодировки: UTF-8 BOM → UTF-8 → CP1251 → Latin-1.
        /// </summary>
        private static string[] ReadCueLines(string path)
        {
            var    bytes = File.ReadAllBytes(path);
            string[] sep = new[] { "\r\n", "\n" };

            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split(sep, StringSplitOptions.None);

            // UTF-8 без BOM
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
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

        private static string ExtractQuotedOrRaw(string line, string prefix)
        {
            string rest = line.Substring(prefix.Length).Trim();
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
            return TimeSpan.FromSeconds(mm * 60 + ss + ff / 75.0);
        }
    }
}
