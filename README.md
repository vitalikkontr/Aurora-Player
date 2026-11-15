<div align="center">

<img width="420" alt="Aurora Player Logo" src="https://github.com/user-attachments/assets/06e8e064-b76e-495b-8d22-edc874efa0df" />

# 🎵 Aurora Player

**Современный аудиоплеер для Windows с поддержкой Lossless-форматов, многоканальным микшером, эквалайзером и визуализатором.**

<p>
  <a href="https://github.com/vitalikkontr/Aurora-Player/releases/latest"><kbd> ⬇️ Скачать v1.4.5.0 </kbd></a>&nbsp;
  <kbd> 🪟 Windows 10 / 11 (x64) </kbd>&nbsp;
  <a href="https://dotnet.microsoft.com/download/dotnet/8"><kbd> 🟣 .NET 8.0 </kbd></a>&nbsp;
  <kbd> 📄 MIT License </kbd>
</p>

[**📋 Список изменений**](https://github.com/vitalikkontr/Aurora-Player/releases) • [**🐞 Сообщить об ошибке**](https://github.com/vitalikkontr/Aurora-Player/issues)

</div>

---

## ✨ Возможности

* 🎧 **Широкая поддержка форматов:** нативное воспроизведение и декодирование через FFmpeg (`MP3`, `FLAC`, `OGG`, `AAC`, `WAV`, `OPUS`, `APE`, `WavPack`, `M4A`, `WMA`, `MKV` и др.).
* 🔊 **Продвинутый аудио-движок:** вывод звука через **WASAPI**, **WinMM** и **ASIO**, поддержка многоканального звука **5.1 / 7.1** с низким потреблением CPU.
* 🎛 **Эквалайзер и DSP:** плавная настройка частот без щелчков, компрессор и пространственный эффект Хааса.
* 🎨 **Визуализатор и кастомизация:** плавные спектральные режимы (60+ FPS, 0 аллокаций в кадре), поддержка HiDPI и настраиваемые цветовые темы.
* 📜 **Работа с метаданными:** полноценная поддержка CUE-листов (`Windows-1251`, `UTF-16 LE/BE`) и ID3/Vorbis/APE тегов с обложками.

---

## 🎯 Поддерживаемые форматы

| Категория | Форматы |
| :--- | :--- |
| **Lossless (без потерь)** | `FLAC`, `WAV`, `APE`, `WavPack (WV)`, `ALAC` |
| **Lossy (сжатые)** | `MP3`, `AAC`, `OGG Vorbis`, `OPUS`, `M4A`, `WMA` |
| **Плейлисты и контейнеры** | `CUE`, `M3U`, `M3U8`, аудиодорожки из `MKV` / `MP4` |

---

## 📋 Системные требования

| Компонент | Минимальные требования |
| :--- | :--- |
| **Операционная система** | Windows 10 / Windows 11 (только **x64**) |
| **Графика** | Видеокарта с поддержкой **DirectX 9** или выше (для аппаратного ускорения WPF) |
| **Среда выполнения** | [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8) |

---

## 📥 Установка и настройка

### Шаг 1. Установка .NET 8 Runtime
Для запуска приложения необходим **.NET 8 Desktop Runtime**:
1. Перейдите на [страницу загрузки Microsoft .NET 8](https://dotnet.microsoft.com/download/dotnet/8).
2. В блоке **Run desktop apps** выберите **Download x64** и установите пакет.

### Шаг 2. Установка Aurora Player
1. Перейдите в раздел [**Releases**](https://github.com/vitalikkontr/Aurora-Player/releases/latest) (ссылка автоматически ведёт на самый свежий релиз).
2. Скачайте **`AuroraPlayerSetup.exe`** и запустите мастер установки.

### Шаг 3. Подключение `ffmpeg.exe` *(для FLAC, OPUS, AAC и др.)*
> [!IMPORTANT]
> Aurora Player использует **FFmpeg** для декодирования форматов, не поддерживаемых Windows из коробки. Без него часть треков не запустится.

1. Скачайте сборку: [**ffmpeg-release-essentials.zip**](https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip)
2. Распакуйте архив и откройте папку `bin/`.
3. Скопируйте файл **`ffmpeg.exe`** в папку с установленным плеером (рядом с `AuroraPlayer.exe`).

---

<details>
<summary><b>🛠 Для разработчиков: Сборка из исходников и NuGet-зависимости</b></summary>
<br>

### Требования для сборки
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
* Visual Studio 2022 (с компонентом *Разработка классических приложений .NET*) или VS Code + C# Dev Kit

### Клонирование и сборка
```bash
git clone [https://github.com/vitalikkontr/Aurora-Player.git](https://github.com/vitalikkontr/Aurora-Player.git)
cd Aurora-Player
dotnet restore
dotnet build -c Release
