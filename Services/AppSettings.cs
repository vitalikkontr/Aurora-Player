#nullable enable

using System.Collections.Generic;

namespace AuroraPlayer
{
    /// <summary>Данные, сохраняемые между сеансами.</summary>
    public class AppSettings
    {
        // ── Плейлист / позиция ────────────────────────────────────────────────────
        /// <summary>Последняя открытая папка (используется, когда плейлист — это папка).</summary>
        public string? LastFolder { get; set; }
        /// <summary>Пути всех треков плейлиста (используется, когда файлы открыты по одному).</summary>
        public List<string>? SavedPaths { get; set; }
        public int LastIndex { get; set; } = -1;
        public double LastPosition { get; set; }

        // ── Звук / эффекты ────────────────────────────────────────────────────────
        public double Volume { get; set; } = 0.75;
        public bool Shuffle { get; set; }
        public bool Repeat { get; set; }
        public bool SurroundOn { get; set; }
        public float SurroundWidth { get; set; } = 0.5f;
        public float[]? EqGains { get; set; }
        public int EqPreset { get; set; } = -1;

        // ── Микшер ───────────────────────────────────────────────────────────────
        public int MixMode { get; set; }
        public float MixWidth { get; set; } = 1.0f;
        public float MixLfeWeight { get; set; }

        // ── Аудио-устройство ──────────────────────────────────────────────────────
        public string? AudioDeviceName { get; set; }

        // ── Визуализатор ──────────────────────────────────────────────────────────
        public int VizMode { get; set; }
        public double VizLeft { get; set; } = 100;
        public double VizTop { get; set; } = 100;
        public double VizWidth { get; set; } = 760;
        public double VizHeight { get; set; } = 430;

        // ── Окно ─────────────────────────────────────────────────────────────────
        public double WindowLeft { get; set; } = 100;
        public double WindowTop { get; set; } = 100;
        public double WindowWidth { get; set; } = 420;
        public double WindowHeight { get; set; } = 680;
        public bool IsMini { get; set; }
        public double MiniPlayerWidth { get; set; } = 480;

        // ── Цвета темы ────────────────────────────────────────────────────────────
        public string? ColorAccent1 { get; set; }
        public string? ColorAccent2 { get; set; }
        public string? ColorCyan { get; set; }
    }
}

#nullable restore