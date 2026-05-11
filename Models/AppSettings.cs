namespace AuroraPlayer
{
    public class AppSettings
    {
        public string? LastFolder    { get; set; }
        public int     LastIndex     { get; set; } = -1;
        public double  LastPosition  { get; set; } = 0;
        public double  Volume        { get; set; } = 0.75;
        public bool    Shuffle       { get; set; }
        public bool    Repeat        { get; set; }
        public bool    SurroundOn    { get; set; } = true;
        public float   SurroundWidth { get; set; } = 0.5f;
        public float[] EqGains       { get; set; } = new float[5];
        public int     EqPreset      { get; set; } = -1;

        // Мини-плеер
        public double MiniPlayerWidth { get; set; } = 480;
        public double MiniLeft        { get; set; } = double.NaN;
        public double MiniTop         { get; set; } = double.NaN;

        // Размер полного окна (сохраняется при уходе в мини, восстанавливается при возврате)
        public double FullWidth  { get; set; } = 420;
        public double FullHeight { get; set; } = 680;

        // Канальный микшер
        public int   MixMode      { get; set; } = 0;
        public float MixWidth     { get; set; } = 1.0f;
        public float MixLfeWeight { get; set; } = 0.0f;

        // Визуализатор
        public int    VizMode   { get; set; } = 0;
        public double VizLeft   { get; set; } = -1;
        public double VizTop    { get; set; } = -1;
        public double VizWidth  { get; set; } = 760;
        public double VizHeight { get; set; } = 430;

        // Позиция и размер главного окна
        public double WindowLeft   { get; set; } = -1;
        public double WindowTop    { get; set; } = -1;
        public double WindowWidth  { get; set; } = 420;
        public double WindowHeight { get; set; } = 680;
        public bool   IsMini       { get; set; } = false;

        // Вывод звука — имя устройства WaveOut (null = системное по умолчанию)
        public string? AudioDeviceName { get; set; } = null;

        // Цветовая тема (hex-строки, null = цвет по умолчанию)
        public string? ColorAccent1 { get; set; } = null; // фиолетовый #7C6BFF
        public string? ColorAccent2 { get; set; } = null; // розовый    #FF6BB5
        public string? ColorCyan    { get; set; } = null; // бирюзовый  #00E5CC
    }

    /// <summary>Runtime-настройки визуализатора (не сериализуются).</summary>
    public class VisualizerSettings
    {
        public int    Mode   { get; set; } = 0;
        public double Left   { get; set; } = -1;
        public double Top    { get; set; } = -1;
        public double Width  { get; set; } = 760;
        public double Height { get; set; } = 430;
    }
}
