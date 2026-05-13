<img width="3245" height="2306" alt="aurora_player" src="https://github.com/user-attachments/assets/3d0ffd69-6075-4862-9d0d-a562721cee84" />

## 🎵 Aurora Player

Музыкальный плеер для Windows с поддержкой широкого спектра аудиоформатов, эквалайзером, микшером каналов и настраиваемой цветовой темой.

---

## 📋 Системные требования

- **ОС:** Windows 10 / Windows 11
- **Архитектура:** x64
- **DirectX:** поддержка DirectX 9 или выше (для WPF-интерфейса)

---

## ⚙️ Зависимости

### .NET Runtime

Для запуска плеера необходим **.NET 8 Desktop Runtime**:

👉 [Скачать .NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8)

Выбери: **Run desktop apps** → **x64** → **Download**

---

### ffmpeg.exe

Aurora Player использует **ffmpeg** для воспроизведения форматов, не поддерживаемых нативно (FLAC, AAC, OPUS, MKV и др.).

👉 **[Скачать ffmpeg для Windows](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip)**

**Установка:**
1. Скачай архив по ссылке выше
2. Распакуй архив
3. Найди файл `ffmpeg.exe` внутри папки `bin/`
4. Скопируй `ffmpeg.exe` в папку рядом с `AuroraPlayer.exe`

---

## 📥 Установка

1. [Скачайте последнюю версию](https://github.com/vitalikkontr/Aurora-Player/releases/tag/v1.3.0.0)
2. Запустите установщик AuroraPlayerSetup.exe
3. Следуйте инструкциям мастера установки

---

## 📦 NuGet-пакеты (для разработчиков)

Подтягиваются автоматически при сборке:

| Пакет | Назначение |
|---|---|
| NAudio | Воспроизведение аудио (WASAPI, WinMM, ASIO) |
| NAudio.Vorbis | Поддержка формата `.ogg` |
| NVorbis | Декодер Vorbis |
| TagLibSharp | Чтение тегов (исполнитель, обложка, название трека) |

---

## 🔨 Сборка из исходников

**Требования:**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Visual Studio 2022 или VS Code с расширением C#

**Команды:**
```bash
git clone https://github.com/vitalikkontr/Aurora-Player.git
cd Aurora-Player
dotnet restore
dotnet build
```

> После сборки не забудь положить `ffmpeg.exe` рядом с собранным `AuroraPlayer.exe`

---

## 🎯 Поддерживаемые форматы

MP3, FLAC, OGG, AAC, WAV, OPUS, M4A, WMA и другие форматы через ffmpeg.

---

## 📄 Лицензия

MIT

---

*Aurora Player — сделано с ❤️*
