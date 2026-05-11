// MainWindow.Playlist.cs — управление плейлистом, файлы, drag-and-drop
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace AuroraPlayer
{
    public partial class MainWindow
    {
        // ─── Открытие файлов ──────────────────────────────────────────────────────

        private void OpenFolder_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку с музыкой" };
            if (dlg.ShowDialog() == true)
            {
                _isLoadingFolder = false;
                _ = AddFolderAsync(dlg.FolderName, clearFirst: true);
            }
        }

        private void OpenFiles_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Музыкальные файлы|" +
                         string.Join(";", PlaylistService.SupportedExt.Select(x => $"*{x}")) +
                         "|CUE листы|*.cue|Все файлы|*.*",
                Title = "Выберите файлы",
            };
            if (dlg.ShowDialog() != true) return;

            StopEngine(); DisposeCachedReader(); SetPlaying(false);
            Playlist.Clear(); _currentIndex = -1;
            UpdatePlaylistHeader();

            foreach (var f in dlg.FileNames)
            {
                if (IOPath.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    AddCueFile(f);
                else
                    AddTrack(f);
            }
            if (Playlist.Count > 0) LoadTrack(0, autoPlay: false);
        }

        // ─── Добавление треков ────────────────────────────────────────────────────

        private void AddTrack(string path)
        {
            if (!File.Exists(path)) return;
            string ext = IOPath.GetExtension(path);
            if (!PlaylistService.SupportedExtSet.Contains(ext)) return;
            if (Playlist.Any(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

            int idx  = Playlist.Count;
            var item = new PlaylistService().BuildTrackItem(path, idx);
            Playlist.Add(item);
            UpdatePlaylistHeader();
        }

        private void AddCueFile(string cuePath)
        {
            try
            {
                int idx   = Playlist.Count;
                var items = new PlaylistService().BuildCueItems(cuePath, idx);
                foreach (var item in items) Playlist.Add(item);
                UpdatePlaylistHeader();
            }
            catch { }
        }

        private async Task AddFolderAsync(string path, bool clearFirst = false,
            int initialIndex = -1, double initialSeek = 0)
        {
            if (_isLoadingFolder) return;
            _isLoadingFolder = true;
            _lastFolder      = path;
            try
            {
                if (clearFirst)
                {
                    StopEngine(); DisposeCachedReader(); SetPlaying(false);
                    Playlist.Clear(); _currentIndex = -1;
                    InvalidateAlbumArt(clearSources: true);
                    UpdatePlaylistHeader();
                }

                var existing = new HashSet<string>(
                    Playlist.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

                var (tracks, _) = await new PlaylistService()
                    .ScanFolderAsync(path, existing, Playlist.Count);

                foreach (var item in tracks) Playlist.Add(item);
                UpdatePlaylistHeader();

                if (_currentIndex < 0 && Playlist.Count > 0)
                {
                    int idx = initialIndex >= 0 && initialIndex < Playlist.Count ? initialIndex : 0;
                    // Если restore уже выполнялся в этой сессии — не затираем позицию.
                    // Но при явной смене папки (clearFirst=true) обязательно грузим первый трек.
                    if (!clearFirst && _sessionRestoreDone && initialSeek == 0 && initialIndex < 0) return;
                    if (initialSeek > 0 || initialIndex >= 0) _sessionRestoreDone = true;
                    LoadTrack(idx, autoPlay: false, seekSeconds: initialSeek);
                }
            }
            finally { _isLoadingFolder = false; }
        }

        // ─── Drag & Drop ──────────────────────────────────────────────────────────

        private void Window_Drop(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var items = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var item in items)
            {
                if (Directory.Exists(item))
                    _ = AddFolderAsync(item);
                else if (IOPath.GetExtension(item).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    AddCueFile(item);
                else
                    AddTrack(item);
            }
            if (_currentIndex < 0 && Playlist.Count > 0) LoadTrack(0, autoPlay: false);
        }

        // ─── Список треков (UI events) ────────────────────────────────────────────

        private void TrackList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_isProgrammaticSelection || _isTransitioning || _selectionChangedSuppressed) return;
            if (TrackList.SelectedIndex >= 0 && TrackList.SelectedIndex != _currentIndex)
                LoadTrack(TrackList.SelectedIndex, autoPlay: true);
        }

        // PreviewMouseLeftButtonDown нужен потому что дочерние элементы DataTemplate
        // поглощают MouseLeftButtonDown и ListBox не получает клик → SelectionChanged не срабатывает.
        private void TrackList_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (_isProgrammaticSelection || _isTransitioning) return;
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null && hit is not ListBoxItem)
                hit = VisualTreeHelper.GetParent(hit);
            if (hit is not ListBoxItem item) return;
            int idx = TrackList.ItemContainerGenerator.IndexFromContainer(item);
            if (idx < 0) return;

            // Одиночный щелчок по текущему (выбранному) треку должен начать воспроизведение.
            if (idx == _currentIndex)
            {
                if (!_isPlaying) PlayPause_Click(this, null!);
                e.Handled = true;
                return;
            }

            _selectionChangedSuppressed = true;
            try
            {
                LoadTrack(idx, autoPlay: true);
                e.Handled = true;
            }
            finally
            {
                // SelectionChanged может появиться после этого обработчика; подавление выпуска
                // при следующем обращении диспетчера, чтобы избежать дублирования вызовов LoadTrack.
                Dispatcher.BeginInvoke(new Action(() => _selectionChangedSuppressed = false));
            }
        }

        private void TrackList_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (TrackList.SelectedIndex >= 0)
                LoadTrack(TrackList.SelectedIndex, autoPlay: true);
        }

        private void ClearPlaylist_Click(object s, RoutedEventArgs e)
        {
            _timer.Stop(); _vizTimer.Stop(); _eqTimer.Stop();
            StopEngine(); DisposeCachedReader(); SetPlaying(false);
            Playlist.Clear(); _currentIndex = -1;
            SongTitle.Text   = "Выберите музыку";
            SongArtist.Text  = "Добавьте папку или файл";
            FormatBadge.Text = "-"; TagBitrate.Text = "-"; TagGenre.Text = "-";
            CurrentTime.Text = "0:00"; TotalTime.Text = "0:00";
            ProgressSlider.Value = 0; ProgressSlider.IsEnabled = false;
            InvalidateAlbumArt(clearSources: true);
            UpdatePlaylistHeader();
        }

        // ─── Заголовок плейлиста ──────────────────────────────────────────────────

        private void UpdatePlaylistHeader() =>
            PlaylistHeader.Text = Playlist.Count == 0
                ? "Плейлист пуст"
                : $"Плейлист · {Playlist.Count} {TrackWord(Playlist.Count)}";

        private static string TrackWord(int n)
        {
            if (n % 100 is >= 11 and <= 14) return "треков";
            return (n % 10) switch { 1 => "трек", 2 or 3 or 4 => "трека", _ => "треков" };
        }

        // ─── Resize полоса плейлиста ──────────────────────────────────────────────

        private bool   _isResizingPlaylist;
        private double _resizeStartY;
        private double _resizeStartWindowHeight;
        private double _resizeContentHeight;

        private void PlaylistResize_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _isResizingPlaylist      = true;
            _resizeStartY            = PointToScreen(e.GetPosition(this)).Y;
            _resizeStartWindowHeight = ActualHeight;
            _resizeContentHeight     = ActualHeight - (TrackList.ActualHeight > 0 ? TrackList.ActualHeight : 0);
            MinHeight = 0;
            this.CaptureMouse();
            e.Handled = true;
        }

        private void PlaylistResize_MouseMove(object s, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isResizingPlaylist) return;
            double delta = PointToScreen(e.GetPosition(this)).Y - _resizeStartY;
            double newH  = _resizeStartWindowHeight + delta;
            double minH  = _resizeContentHeight > 0 ? _resizeContentHeight : 420;
            Height = Math.Max(newH, minH);
        }

        private void PlaylistResize_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (!_isResizingPlaylist) return;
            _isResizingPlaylist = false;
            this.ReleaseMouseCapture();
            MinHeight = 0;
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isResizingPlaylist) PlaylistResize_MouseMove(this, e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isResizingPlaylist) PlaylistResize_MouseUp(this, e);
        }
    }
}
