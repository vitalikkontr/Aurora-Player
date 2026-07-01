using System.Collections.Generic;

namespace AuroraPlayer
{
    /// <summary>Данные, сохраняемые между сеансами.</summary>
    public class AppSettings
    {
        // ── Плейлист / позиция ────────────────────────────────────────────────────
        /// <summary>Последняя открытая папка (используется когда плейлист — это папка).</summary>
        public string?       LastFolder   { get; set; }
        /// <summary>Пути всех треков плейлиста (используется когда файлы открыты по одному).</summary>
        public List<string>? SavedPaths   { get; set; }
        public int           LastIndex    { get; set; } = -1;
        public double        LastPosition { get; set; }

        // ── Звук / эффекты ────────────────────────────────────────────────────────
        public double  Volume        { get; set; } = 0.8;
        public bool    Shuffle       { get; set; }
        public bool    Repeat        { get; set; }
        public bool    SurroundOn    { get; set; }
        public float   SurroundWidth { get; set; } = 1.0f;
        public float[]? EqGains      { get; set; }
        public int     EqPreset      { get; set; } = -1;

        // ── Микшер ───────────────────────────────────────────────────────────────
        public int   MixMode      { get; set; }
        public float MixWidth     { get; set; } = 1.0f;
        public float MixLfeWeight { get; set; }

        // ── Аудио-устройство ──────────────────────────────────────────────────────
        public string? AudioDeviceName { get; set; }

        // ── Визуализатор ──────────────────────────────────────────────────────────
        public int    VizMode   { get; set; }
        public double VizLeft   { get; set; }
        public double VizTop    { get; set; }
        public double VizWidth  { get; set; }
        public double VizHeight { get; set; }

        // ── Окно ─────────────────────────────────────────────────────────────────
        public double WindowLeft      { get; set; }
        public double WindowTop       { get; set; }
        public double WindowWidth     { get; set; }
        public double WindowHeight    { get; set; }
        public bool   IsMini          { get; set; }
        public double MiniPlayerWidth { get; set; }

        // ── Цвета темы ────────────────────────────────────────────────────────────
        public string? ColorAccent1 { get; set; }
        public string? ColorAccent2 { get; set; }
        public string? ColorCyan    { get; set; }
    }
}
