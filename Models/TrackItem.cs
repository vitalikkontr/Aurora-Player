using System;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace AuroraPlayer
{
    public class TrackItem
    {
        public string      Path      { get; set; } = "";
        public string      Title     { get; set; } = "";
        public string      Artist    { get; set; } = "";
        public string      Duration  { get; set; } = "—";
        public string      Format    { get; set; } = "";
        public int         Index     { get; set; }
        public string      Color1    { get; set; } = "#1A1A3E";
        public string      Color2    { get; set; } = "#2D1B4E";

        /// <summary>
        /// Обложка из тегов. Null — показывать градиентную заглушку.
        /// Берётся из CoverCache по ключу (путь к файлу или папке) —
        /// треки одного альбома не дублируют BitmapSource в памяти.
        /// </summary>
        public BitmapSource? CoverImage
        {
            get => _coverCacheKey != null && CoverCache.TryGetValue(_coverCacheKey, out var bmp) ? bmp : _coverDirect;
            set
            {
                // Если ключ задан — пишем в кэш, иначе храним напрямую (CUE и т.п.)
                if (_coverCacheKey != null && value != null)
                    CoverCache[_coverCacheKey] = value;
                else
                    _coverDirect = value;
            }
        }

        /// <summary>Ключ кэша обложек (обычно путь к папке альбома).</summary>
        public string? CoverCacheKey
        {
            get => _coverCacheKey;
            set => _coverCacheKey = value;
        }

        private string?      _coverCacheKey;
        private BitmapSource? _coverDirect;

        /// <summary>Глобальный кэш обложек: ключ → BitmapSource. Очищать при смене библиотеки.</summary>
        public static readonly ConcurrentDictionary<string, BitmapSource> CoverCache = new();

        // CUE: смещение внутри большого файла (TimeSpan.Zero = обычный файл)
        public TimeSpan CueStart { get; set; } = TimeSpan.Zero;
        public TimeSpan CueEnd   { get; set; } = TimeSpan.Zero;
        public bool     IsCue    { get; set; } = false;
    }
}
