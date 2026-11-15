#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace AuroraPlayer
{
    public class TrackItem : INotifyPropertyChanged
    {
        private const int MaxCoverCacheEntries = 256;

        private string _path = "";
        private string _title = "";
        private string _artist = "";
        private string _duration = "—";
        private string _format = "";
        private int _index;
        private string _color1 = "#1A1A3E";
        private string _color2 = "#2D1B4E";
        private string? _coverCacheKey;
        private BitmapSource? _coverDirect;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path
        {
            get => _path;
            set => SetField(ref _path, value ?? "");
        }

        public string Title
        {
            get => _title;
            set => SetField(ref _title, value ?? "");
        }

        public string Artist
        {
            get => _artist;
            set => SetField(ref _artist, value ?? "");
        }

        public string Duration
        {
            get => _duration;
            set => SetField(ref _duration, value ?? "—");
        }

        public string Format
        {
            get => _format;
            set => SetField(ref _format, value ?? "");
        }

        public int Index
        {
            get => _index;
            set => SetField(ref _index, value);
        }

        public string Color1
        {
            get => _color1;
            set => SetField(ref _color1, value ?? "#1A1A3E");
        }

        public string Color2
        {
            get => _color2;
            set => SetField(ref _color2, value ?? "#2D1B4E");
        }

        /// <summary>
        /// Обложка из тегов. Null — показывать градиентную заглушку.
        /// Берётся из CoverCache по ключу (путь к файлу или папке) —
        /// треки одного альбома не дублируют BitmapSource в памяти.
        /// </summary>
        public BitmapSource? CoverImage
        {
            get
            {
                string? key = Volatile.Read(ref _coverCacheKey);
                if (key != null && CoverCache.TryGetValue(key, out var bmp))
                    return bmp;

                return Volatile.Read(ref _coverDirect);
            }
            set
            {
                if (value != null && !value.IsFrozen && value.CanFreeze)
                {
                    try { value.Freeze(); }
                    catch { }
                }

                string? key = Volatile.Read(ref _coverCacheKey);
                if (key != null && value != null)
                {
                    bool isNew = !CoverCache.ContainsKey(key);
                    CoverCache[key] = value;
                    if (isNew)
                    {
                        _coverCacheOrder.Enqueue(key);
                        TrimCoverCacheIfNeeded();
                    }
                    Volatile.Write(ref _coverDirect, null);
                }
                else
                {
                    Volatile.Write(ref _coverDirect, value);
                }

                OnPropertyChanged();
            }
        }

        /// <summary>Ключ кэша обложек (обычно путь к папке альбома).</summary>
        public string? CoverCacheKey
        {
            get => Volatile.Read(ref _coverCacheKey);
            set
            {
                string? prev = Volatile.Read(ref _coverCacheKey);
                if (string.Equals(prev, value, StringComparison.OrdinalIgnoreCase))
                    return;

                Volatile.Write(ref _coverCacheKey, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CoverImage));
            }
        }

        /// <summary>Глобальный кэш обложек: ключ → BitmapSource (без учёта регистра пути).</summary>
        public static readonly ConcurrentDictionary<string, BitmapSource> CoverCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentQueue<string> _coverCacheOrder = new();

        public static void ClearCoverCache()
        {
            CoverCache.Clear();
            while (_coverCacheOrder.TryDequeue(out _)) { }
        }

        private static void TrimCoverCacheIfNeeded()
        {
            while (CoverCache.Count > MaxCoverCacheEntries &&
                   _coverCacheOrder.TryDequeue(out var oldest))
            {
                CoverCache.TryRemove(oldest, out _);
            }
        }

        // CUE: смещение внутри большого файла (TimeSpan.Zero = обычный файл)
        public TimeSpan CueStart { get; set; } = TimeSpan.Zero;
        public TimeSpan CueEnd { get; set; } = TimeSpan.Zero;
        public bool IsCue { get; set; } = false;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            OnPropertyChanged(propertyName);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

#nullable restore