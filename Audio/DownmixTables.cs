// DownmixTables.cs — единственный источник матрицы даунмикса ITU-R BS.775.
// Используется в ChannelMixerProvider и SurroundProvider.
namespace AuroraPlayer
{
    internal static class DownmixTables
    {
        // Порядок каналов: FL FR FC LFE BL BR SL SR
        // Значения: -3 dB для центра/сурраунда (sqrt(0.5) ≈ 0.707)
        public static readonly float[] L = { 1.0f, 0.0f, 0.707f, 0.0f, 0.707f, 0.0f,   0.707f, 0.0f   };
        public static readonly float[] R = { 0.0f, 1.0f, 0.707f, 0.0f, 0.0f,   0.707f, 0.0f,   0.707f };
    }
}
