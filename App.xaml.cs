using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace AuroraPlayer
{
    public partial class App : Application
    {
        private const string MutexName = "AuroraPlayer_SingleInstance";
        private const string PipeName  = "AuroraPlayer_IPC";

        private Mutex?  _mutex;
        private Thread? _pipeThread;
        private bool    _ownsMutex;

        public App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);

            if (!_ownsMutex)
            {
                // Плеер уже запущен — передаём путь первому экземпляру и выходим
                string arg = e.Args.Length > 0 ? e.Args[0] : "";
                SendToRunningInstance(arg);
                _mutex.Dispose();
                Shutdown();
                return;
            }

            // Мы первый экземпляр — запускаем pipe-сервер
            StartPipeServer();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeThread = null; // сигнал потоку на выход
            if (_ownsMutex) try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }

        // ─── Отправка пути к уже запущенному плееру ──────────────────────────────

        private static void SendToRunningInstance(string filePath)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName,
                    PipeDirection.Out, PipeOptions.None);
                client.Connect(timeout: 2000);
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.WriteLine(filePath);
                writer.Flush();
            }
            catch { }
        }

        // ─── Pipe-сервер в фоновом потоке ────────────────────────────────────────

        private void StartPipeServer()
        {
            _pipeThread = new Thread(PipeServerLoop)
            {
                IsBackground = true,
                Name         = "AuroraPlayer.PipeServer"
            };
            _pipeThread.Start();
        }

        private void PipeServerLoop()
        {
            while (_pipeThread != null)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName,
                        PipeDirection.In, 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    server.WaitForConnection();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? path = reader.ReadLine();

                    Dispatcher.Invoke(() => OnFileReceivedFromPipe(path ?? ""));
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }

        // ─── Получен путь от второго экземпляра ──────────────────────────────────

        private void OnFileReceivedFromPipe(string path)
        {
            if (MainWindow is not MainWindow player) return;

            // Поднимаем окно на передний план
            if (player.WindowState == WindowState.Minimized)
                player.WindowState = WindowState.Normal;
            player.Activate();
            player.Focus();

            if (!string.IsNullOrEmpty(path))
                player.OpenFromCommandLine(path);
        }
    }
}
