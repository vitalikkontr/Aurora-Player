// MainWindow.Playlist.cs — управление плейлистом, файлы, drag-and-drop
#nullable enable

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
        private int _folderLoadGeneration;

        // ─── Открытие файлов ──────────────────────────────────────────────────────

        private void OpenFolder_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Выберите папку с музыкой" };
            if (dlg.ShowDialog() == true)
                _ = AddFolderAsync(dlg.FolderName, clearFirst: true);
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

            Interlocked.Increment(ref _folderLoadGeneration);
            _isLoadingFolder = false;

            StopEngine();
            DisposeCachedReader();
            SetPlaying(false);
            _lastFolder = null; // файлы открыты по одному — восстановление через SavedPaths
            Playlist.Clear();
            _currentIndex = -1;
            InvalidateAlbumArt(clearSources: true);
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            string ext = IOPath.GetExtension(path);
            if (!PlaylistService.SupportedExtSet.Contains(ext)) return;
            if (Playlist.Any(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

            int idx = Playlist.Count;
            var item = new PlaylistService().BuildTrackItem(path, idx);
            Playlist.Add(item);
            UpdatePlaylistHeader();
        }

        private void AddCueFile(string cuePath)
        {
            if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath)) return;
            try
            {
                int idx = Playlist.Count;
                var items = new PlaylistService().BuildCueItems(cuePath, idx);
                if (items == null) return;

                foreach (var item in items)
                {
                    bool exists = Playlist.Any(t =>
                        t.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) &&
                        t.CueStart == item.CueStart &&
                        t.CueEnd == item.CueEnd);
                    if (!exists)
                        Playlist.Add(item);
                }
                UpdatePlaylistHeader();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddCueFile error: {ex}");
            }
        }

        private async Task AddFolderAsync(string path, bool clearFirst = false,
            int initialIndex = -1, double initialSeek = 0)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            int loadGen = clearFirst
                ? Interlocked.Increment(ref _folderLoadGeneration)
                : Volatile.Read(ref _folderLoadGeneration);

            _isLoadingFolder = true;
            _lastFolder = path;
            try
            {
                if (clearFirst)
                {
                    StopEngine();
                    DisposeCachedReader();
                    SetPlaying(false);
                    Playlist.Clear();
                    _currentIndex = -1;
                    InvalidateAlbumArt(clearSources: true);
                    UpdatePlaylistHeader();
                }

                var existing = new HashSet<string>(
                    Playlist.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

                var (tracks, _) = await new PlaylistService()
                    .ScanFolderAsync(path, existing, Playlist.Count);

                // Если во время сканирования открыли другую папку/файлы или закрыли окно — выходим
                if (_isClosingHandled || loadGen != Volatile.Read(ref _folderLoadGeneration))
                    return;

                if (tracks != null)
                {
                    foreach (var item in tracks)
                    {
                        if (!Playlist.Any(t => t.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) &&
                                               t.CueStart == item.CueStart && t.CueEnd == item.CueEnd))
                        {
                            Playlist.Add(item);
                        }
                    }
                }
                UpdatePlaylistHeader();

                if (_currentIndex < 0 && Playlist.Count > 0)
                {
                    int idx = initialIndex >= 0 && initialIndex < Playlist.Count ? initialIndex : 0;
                    if (!clearFirst && _sessionRestoreDone && initialSeek == 0 && initialIndex < 0)
                        return;
                    if (initialSeek > 0 || initialIndex >= 0)
                        _sessionRestoreDone = true;

                    LoadTrack(idx, autoPlay: false, seekSeconds: initialSeek);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddFolderAsync error: {ex}");
            }
            finally
            {
                if (loadGen == Volatile.Read(ref _folderLoadGeneration))
                    _isLoadingFolder = false;
            }
        }

        // ─── Drag & Drop ──────────────────────────────────────────────────────────

        private async void Window_Drop(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0)
                return;

            try
            {
                foreach (var item in items)
                {
                    if (_isClosingHandled) return;
                    if (string.IsNullOrWhiteSpace(item)) continue;

                    if (Directory.Exists(item))
                        await AddFolderAsync(item);
                    else if (IOPath.GetExtension(item).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                        AddCueFile(item);
                    else
                        AddTrack(item);
                }

                if (!_isClosingHandled && _currentIndex < 0 && Playlist.Count > 0)
                    LoadTrack(0, autoPlay: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Window_Drop error: {ex}");
            }
        }

        // ─── Список треков (UI events) ────────────────────────────────────────────

        private void TrackList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_isProgrammaticSelection || _isTransitioning || _selectionChangedSuppressed) return;
            if (TrackList.SelectedIndex >= 0 &&
                TrackList.SelectedIndex < Playlist.Count &&
                TrackList.SelectedIndex != _currentIndex)
            {
                LoadTrack(TrackList.SelectedIndex, autoPlay: true);
            }
        }

        private void TrackList_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            if (_isProgrammaticSelection || _isTransitioning) return;

            var hit = e.OriginalSource as DependencyObject;
            while (hit != null && hit is not ListBoxItem)
            {
                hit = hit is Visual
                    ? VisualTreeHelper.GetParent(hit)
                    : LogicalTreeHelper.GetParent(hit);
            }
            if (hit is not ListBoxItem item) return;

            int idx = TrackList.ItemContainerGenerator.IndexFromContainer(item);
            if (idx < 0 || idx >= Playlist.Count) return;

            if (idx == _currentIndex)
            {
                if (!_isPlaying) PlayPause_Click(this, e);
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
                Dispatcher.BeginInvoke(new Action(() => _selectionChangedSuppressed = false));
            }
        }

        private void TrackList_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (TrackList.SelectedIndex >= 0 && TrackList.SelectedIndex < Playlist.Count)
                LoadTrack(TrackList.SelectedIndex, autoPlay: true);
        }

        private void ClearPlaylist_Click(object s, RoutedEventArgs e)
        {
            Interlocked.Increment(ref _folderLoadGeneration);
            _isLoadingFolder = false;
            _lastFolder = null;

            _timer.Stop();
            _vizTimer.Stop();
            _eqTimer.Stop();
            StopEngine();
            DisposeCachedReader();
            SetPlaying(false);

            Playlist.Clear();
            _currentIndex = -1;

            SongTitle.Text = "Выберите музыку";
            SongArtist.Text = "Добавьте папку или файл";
            MiniTitle.Text = "Aurora Player";
            MiniArtist.Text = "";
            FormatBadge.Text = "-";
            TagBitrate.Text = "-";
            TagGenre.Text = "-";
            CurrentTime.Text = "0:00";
            TotalTime.Text = "0:00";
            ProgressSlider.Maximum = 0;
            ProgressSlider.Value = 0;
            ProgressSlider.IsEnabled = false;

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

        private bool _isResizingPlaylist;
        private double _resizeStartY;
        private double _resizeStartWindowHeight;
        private double _resizeContentHeight;

        private void PlaylistResize_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || _isMini) return;
            if (PresentationSource.FromVisual(this) == null) return;

            _isResizingPlaylist = true;
            _resizeStartY = PointToScreen(e.GetPosition(this)).Y;
            _resizeStartWindowHeight = ActualHeight;
            double trackH = TrackList != null && TrackList.ActualHeight > 0 ? TrackList.ActualHeight : 0;
            _resizeContentHeight = ActualHeight - trackH;
            MinHeight = 0;

            LostMouseCapture -= PlaylistResize_LostMouseCapture;
            LostMouseCapture += PlaylistResize_LostMouseCapture;
            CaptureMouse();
            e.Handled = true;
        }

        private void PlaylistResize_MouseMove(object s, MouseEventArgs e)
        {
            if (!_isResizingPlaylist) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishPlaylistResize();
                return;
            }

            if (PresentationSource.FromVisual(this) is not PresentationSource src ||
                src.CompositionTarget == null)
                return;

            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            if (dpiY <= 0) dpiY = 1.0;

            double deltaDip = (PointToScreen(e.GetPosition(this)).Y - _resizeStartY) / dpiY;
            double newH = _resizeStartWindowHeight + deltaDip;
            double minH = _resizeContentHeight > 0 ? _resizeContentHeight : 420;

            var wa = GetCurrentMonitorWorkAreaDip();
            double maxH = Math.Max(minH, wa.Bottom - Top);

            Height = Math.Clamp(newH, minH, maxH);
        }

        private void PlaylistResize_MouseUp(object s, MouseButtonEventArgs e)
        {
            FinishPlaylistResize();
        }

        private void PlaylistResize_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FinishPlaylistResize();
        }

        private void FinishPlaylistResize()
        {
            if (!_isResizingPlaylist) return;
            _isResizingPlaylist = false;
            LostMouseCapture -= PlaylistResize_LostMouseCapture;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            MinHeight = 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
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

#nullable restore