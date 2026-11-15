#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AuroraPlayer
{
    public partial class App : Application
    {
        private const string MutexName = "AuroraPlayer_SingleInstance";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private Mutex? _mutex;
        private Thread? _pipeThread;
        private CancellationTokenSource? _pipeCts;
        private bool _ownsMutex;

        public App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private static string GetPipeName()
        {
            using var proc = Process.GetCurrentProcess();
            return $"AuroraPlayer_IPC_{proc.SessionId}";
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);

            if (!_ownsMutex)
            {
                string arg = e.Args.Length > 0 ? e.Args[0] : "";
                SendToRunningInstance(arg);
                _mutex.Dispose();
                _mutex = null;
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
            StartPipeServer();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _pipeCts?.Cancel(); } catch { }
            try { _pipeThread?.Join(300); } catch { }
            _pipeThread = null;
            _pipeCts?.Dispose();
            _pipeCts = null;

            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
            }
            _mutex?.Dispose();
            _mutex = null;

            base.OnExit(e);
        }

        // ─── Отправка пути к уже запущенному плееру ──────────────────────────────

        private static void SendToRunningInstance(string filePath)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    GetPipeName(),
                    PipeDirection.Out,
                    PipeOptions.CurrentUserOnly);
                client.Connect(timeout: 2000);
                using var writer = new StreamWriter(client, Utf8NoBom);
                writer.WriteLine(filePath);
                writer.Flush();
            }
            catch { }
        }

        // ─── Pipe-сервер в фоновом потоке ────────────────────────────────────────

        private void StartPipeServer()
        {
            _pipeCts = new CancellationTokenSource();
            var token = _pipeCts.Token;

            _pipeThread = new Thread(() => PipeServerLoop(token))
            {
                IsBackground = true,
                Name = "AuroraPlayer.PipeServer"
            };
            _pipeThread.Start();
        }

        private void PipeServerLoop(CancellationToken token)
        {
            string pipeName = GetPipeName();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();

                    using var cancelReg = token.Register(() =>
                    {
                        try { server.Dispose(); } catch { }
                    });

                    using var reader = new StreamReader(server, Utf8NoBom);
                    string? path = reader.ReadLine();

                    if (path != null &&
                        !token.IsCancellationRequested &&
                        !Dispatcher.HasShutdownStarted &&
                        !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.BeginInvoke(new Action(() => OnFileReceivedFromPipe(path)));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (token.WaitHandle.WaitOne(300))
                        break;
                }
            }
        }

        // ─── Получен путь от второго экземпляра ──────────────────────────────────

        private void OnFileReceivedFromPipe(string path, int retriesLeft = 10)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (MainWindow is not MainWindow player || !player.IsLoaded)
            {
                if (retriesLeft > 0)
                {
                    Dispatcher.BeginInvoke(
                        new Action(() => OnFileReceivedFromPipe(path, retriesLeft - 1)),
                        DispatcherPriority.Loaded);
                }
                return;
            }

            if (player.WindowState == WindowState.Minimized)
                player.WindowState = WindowState.Normal;
            player.Activate();
            player.Focus();

            if (!string.IsNullOrWhiteSpace(path))
                player.OpenFromCommandLine(path);
        }
    }
}

#nullable restore